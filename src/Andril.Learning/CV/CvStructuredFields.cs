using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Andril.Learning.CV;

/// <summary>Confirmed AND-M1-B410-CV-FIELDS-20261127. No legacy text is automatically classified.</summary>
public static class CvStructuredFields
{
    private static readonly IReadOnlyDictionary<CvSectionCategory, string[]> Allowed = new Dictionary<CvSectionCategory,string[]>
    {
        [CvSectionCategory.PersonalInformation] = ["ProfessionalDisplayName","ProfessionalTitle","Email","Phone","CityCountry","ProfessionalProfileLinks"],
        [CvSectionCategory.ProfessionalSummary] = ["ProfessionalObjective","CareerSummary","ProfessionalStrengths"],
        [CvSectionCategory.Education] = ["Institution","Qualification","FieldOfStudy","StartDate","EndDate","GradeOrAcademicResult"],
        [CvSectionCategory.WorkExperience] = ["Organization","JobTitle","StartDate","EndDate","Responsibilities","ProfessionalAchievements"],
        [CvSectionCategory.Skills] = ["SkillName","Proficiency","SkillCategory"],
        [CvSectionCategory.Projects] = ["ProjectTitle","Description","Technologies","Contribution","Outcomes"],
        [CvSectionCategory.CertificationsAndAchievements] = ["CertificationOrAchievementTitle","IssuingOrganization","AwardDate","ExpiryDate","Description"]
    };
    private static readonly HashSet<string> Contact = ["Email","Phone","CityCountry","ProfessionalProfileLinks"];
    // Conservative withholding, never a claim that free text can prove absence of all secrets.
    // Boolean classification needs no captures. Linear-time matching prevents long
    // nonmatching tokens from exhausting the unchanged safety timeout during PDF GC.
    private const RegexOptions ClassificationOptions=RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex EmbeddedContact = new(@"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|(?:\+?\d[\s().-]*){7,}|(?:https?://|www\.)", ClassificationOptions, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Prohibited = new(@"\b(password|passwd|api[_ -]?key|secret[_ -]?key|private[_ -]?key|government[_ -]?id|passport|national[_ -]?id|ssn|iban|bank[_ -]?account|credit[_ -]?card|diary|residential[_ -]?address)\b|\b\d{3}-\d{2}-\d{4}\b|\b(?:\d[ -]?){13,19}\b|\b\d+\s+[^\r\n,]{1,60}\s+(street|road|avenue|lane|drive)\b", ClassificationOptions, TimeSpan.FromMilliseconds(100));
    public static IReadOnlyList<string> Names(CvSectionCategory category) => Array.AsReadOnly(Allowed.TryGetValue(category,out var names) ? names.ToArray() : throw new ArgumentException("Unsupported CV section."));
    internal static IReadOnlyDictionary<string,string> Validate(CvSectionCategory category, IReadOnlyDictionary<string,string>? fields)
    {
        var copy = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var pair in fields ?? new Dictionary<string,string>())
        {
            if (!Allowed.TryGetValue(category,out var names) || !names.Contains(pair.Key,StringComparer.Ordinal) || pair.Value is null ||
                pair.Value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) || !copy.TryAdd(pair.Key,pair.Value))
                throw new ArgumentException("Unsupported or invalid structured CV field.");
            if (Prohibited.IsMatch(pair.Value)) throw new ArgumentException("Prohibited private content cannot be classified as a CV field.");
            if (pair.Key == "Email" && pair.Value.Length > 0 && (!System.Net.Mail.MailAddress.TryCreate(pair.Value,out var address) || address.Address != pair.Value))
                throw new ArgumentException("Invalid contact email.");
            if ((pair.Key is "StartDate" or "EndDate" or "AwardDate" or "ExpiryDate") && pair.Value.Length > 0 &&
                !DateOnly.TryParseExact(pair.Value,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))
                throw new ArgumentException("Structured dates require an ISO calendar date.");
            if (Regex.IsMatch(pair.Value,@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                throw new ArgumentException("Internal identity references are not renderable CV content.");
            if (pair.Key == "ProfessionalProfileLinks")
                foreach(var link in pair.Value.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries))
                    if (!Uri.TryCreate(link,UriKind.Absolute,out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
                        throw new ArgumentException("Professional links require HTTPS without credentials.");
        }
        return new ReadOnlyDictionary<string,string>(copy);
    }
    internal static bool MayRender(string key,string value,bool contactConsent)
        => !Prohibited.IsMatch(value) && (Contact.Contains(key) ? contactConsent :
            (key is "StartDate" or "EndDate" or "AwardDate" or "ExpiryDate")
                ? DateOnly.TryParseExact(value,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _)
                : !EmbeddedContact.IsMatch(value));
    public static CvSection.Entry ReviewedEntry(Guid id,Guid sectionId,CvSectionCategory category,IReadOnlyDictionary<string,string> fields)
        => new(id,sectionId,null,Validate(category,fields));
}
