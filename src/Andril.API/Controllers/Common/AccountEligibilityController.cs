using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Andril.Common.Accounts.Eligibility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/account/eligibility")]
[EligibilityNoStore]
public sealed class AccountEligibilityController(IAccountEligibilityService eligibility) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AccountEligibilityDto>> Get(CancellationToken cancellationToken) =>
        Ok(await eligibility.GetAsync(cancellationToken));

    [HttpPut]
    public async Task<ActionResult<AccountEligibilityDto>> Submit([FromBody] AccountEligibilityRequest request,
        CancellationToken cancellationToken) =>
        Ok(await eligibility.SubmitAsync(request.DateOfBirth, request.Gender, cancellationToken));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AccountEligibilityRequest
{
    [Required(ErrorMessage = "dateOfBirth is required.")]
    public DateOnly? DateOfBirth { get; init; }

    [Required(ErrorMessage = "gender is required.")]
    [StringLength(GenderInputPolicy.MaximumLength, ErrorMessage = "gender exceeds the technical input limit.")]
    public string? Gender { get; init; }
}

public sealed class EligibilityNoStoreAttribute : Attribute, IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context) => context.HttpContext.Response.Headers.CacheControl = "no-store";
    public void OnResultExecuted(ResultExecutedContext context) { }
}
