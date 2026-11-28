using System.Data;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Owner=Andril.Persistence.Repositories.Diary.IsolatedModuleDiaryRepository.Owner;

namespace Andril.Persistence.Repositories.Diary;

/// <summary>Private Learning Diary only; does not activate Hobbies/Social or expose Diary to AI.</summary>
public static class LearningDiaryPersistence
{
    public static IModuleDiaryRepository Create(AccountsDbContext accounts,ICurrentUser user,IPrivateProfileService profiles,string role)=>
        new IsolatedModuleDiaryRepository(new LearningDiaryTransaction(accounts,user,profiles,role));
}
internal sealed class DiaryTransactionConflictException:Exception;

internal sealed class LearningDiaryTransaction(AccountsDbContext accounts,ICurrentUser user,IPrivateProfileService profiles,string role)
{
    private readonly LearningOwnerDocumentTransaction boundary=new(accounts,user,profiles,role,LearningPrivateDocument.Diary);
    private Owner? lease;
    internal void Revalidate(Owner owner,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(owner!=lease||!user.IsAuthenticated||user.UserId!=owner.Subject||owner.Module!="learning"||
            accounts.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel!=IsolationLevel.Serializable)
            throw new ProfileAccessDeniedException();
    }
    internal DiaryEntrySnapshot Snapshot(ModuleDiaryEntry entry,CancellationToken ct)
    {
        var owner=lease??throw new ProfileAccessDeniedException();Revalidate(owner,ct);
        if(entry.Module!=owner.Module||entry.OwnerModuleProfileId!=owner.Profile)throw new ProfileAccessDeniedException();
        return new DiaryEntrySnapshot(entry);
    }
    internal Task<T> Run<T>(string module,Func<DiaryDbContext,Owner,CancellationToken,Task<T>> action,
        Func<Exception,T> failure,CancellationToken ct,Func<T>? unknown)
    {
        ct.ThrowIfCancellationRequested();
        if(module!="learning")return Task.FromResult(failure(new ProfileAccessDeniedException()));
        return boundary.Run(async(learning,owner,token)=>
        {
            await using var db=new DiaryDbContext(new DbContextOptionsBuilder<DiaryDbContext>().UseNpgsql(learning.Database.GetDbConnection()).Options);
            await db.Database.UseTransactionAsync(learning.Database.CurrentTransaction!.GetDbTransaction(),token);
            lease=new(owner.Subject,owner.Profile,"learning");
            try{return await action(db,lease,token);}finally{lease=null;}
        },state=>state switch
        {
            LearningDocumentFailure.Denied=>failure(new ProfileAccessDeniedException()),
            LearningDocumentFailure.Conflict=>failure(new DiaryTransactionConflictException()),
            LearningDocumentFailure.CommitUnknown when unknown is not null=>unknown(),
            _=>failure(new ProfileContractPendingException())
        },ct,unknown is not null,additionalConflict:error=>error is DbUpdateException{InnerException:PostgresException{SqlState:PostgresErrorCodes.UniqueViolation,ConstraintName:"pk_module_diary_entries"}});
    }
}
