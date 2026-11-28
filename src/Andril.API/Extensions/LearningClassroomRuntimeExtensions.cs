using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.API.Integrations.Learning;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.API.Extensions;

/// <summary>Explicit ordinary Learning discovery over the verified M5 PostgreSQL authority.
/// Installs no AI disclosure permission, Learning document storage, worker or provider.</summary>
public static class LearningClassroomRuntimeExtensions
{
    private sealed record Enabled;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Encoder=JavaScriptEncoder.Default};

    public static IServiceCollection AddVerifiedLearningClassrooms(this IServiceCollection services,IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);ArgumentNullException.ThrowIfNull(configuration);
        if(!configuration.GetValue<bool>("Learning:Classrooms:Enabled"))return services;
        if(!configuration.GetValue<bool>("SharedGroups:EnablePersistence") || !configuration.GetValue<bool>("SharedGroups:EnableLearning"))
            throw new InvalidOperationException("Learning Classrooms requires verified Shared Groups persistence and Learning authorization.");
        services.TryAddSingleton(new Enabled());
        services.TryAddScoped<LearningClassroomDiscoveryService>();
        services.AddHostedService<LearningClassroomReadiness>();
        return services;
    }

    public static WebApplication MapVerifiedLearningClassrooms(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if(app.Services.GetService<Enabled>() is null)return app;
        app.MapGet("/api/learning/classrooms",ReadAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ReadAsync(HttpContext context,string? query,int? limit,
        LearningClassroomDiscoveryService classrooms,CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl="no-store";
        context.Response.Headers.Pragma="no-cache";
        try
        {
            var result=await classrooms.SearchAsync(query,limit??50,cancellationToken);
            return Results.Json(result,Json,statusCode:result.Outcome switch
            {"Available"=>200,"Denied"=>403,"Invalid"=>400,_=>503});
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
        catch(Exception){return Results.Json(new LearningClassroomResult("Unavailable"),Json,statusCode:503);}
    }

    // Read-only readiness, not a cleanup worker or migration runner. In particular,
    // a superuser/BYPASSRLS definer is not accepted as evidence that FORCE RLS works.
    private sealed class LearningClassroomReadiness(IServiceScopeFactory scopes):IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var scope=scopes.CreateAsyncScope();
                var db=scope.ServiceProvider.GetRequiredService<GroupsDbContext>();
                var verified=await db.Database.SqlQueryRaw<bool>("""
                    SELECT EXISTS(SELECT FROM pg_proc f JOIN pg_roles owner ON owner.oid=f.proowner
                        JOIN pg_class s ON s.oid=to_regclass('andril_groups.learning_eligibility_read_scopes')
                        JOIN pg_class l ON l.oid=to_regclass('andril_identity.account_module_lifecycles')
                        WHERE f.oid=to_regprocedure('andril_groups.learning_group_target_eligible(uuid,uuid)')
                            AND f.prosecdef AND NOT owner.rolsuper AND NOT owner.rolbypassrls
                            AND s.relowner=f.proowner AND s.relrowsecurity AND s.relforcerowsecurity
                            AND l.relrowsecurity AND l.relforcerowsecurity
                            AND NOT EXISTS(SELECT FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner))) a WHERE a.grantee=0)
                            AND NOT EXISTS(SELECT FROM aclexplode(coalesce(s.relacl,acldefault('r',s.relowner))) a WHERE a.grantee=0)
                            AND EXISTS(SELECT FROM pg_policy p WHERE p.polrelid=l.oid AND p.polname='learning_group_eligibility_scope'
                                AND p.polcmd='r' AND p.polroles=ARRAY[f.proowner])
                            AND EXISTS(SELECT FROM pg_policy p WHERE p.polrelid=l.oid AND p.polname='learning_group_eligibility_lock'
                                AND p.polcmd='w' AND p.polroles=ARRAY[f.proowner] AND pg_get_expr(p.polwithcheck,p.polrelid)='false')) AS "Value"
                    """).SingleAsync(cancellationToken);
                if(!verified)throw new InvalidOperationException("Learning eligibility authority is unavailable.");
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
            catch(Exception){throw new InvalidOperationException("Learning eligibility authority is unavailable.");}
        }
        public Task StopAsync(CancellationToken cancellationToken)=>Task.CompletedTask;
    }
}
