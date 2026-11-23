using System.Text;
using System.Text.Json;
using Andril.Social.Personality;

namespace Andril.API.Contracts.Social;

public sealed record SocialQuestionnaireParseResult(
    bool IsSuccess,
    QuestionnaireSubmission? Submission,
    string? Error)
{
    public static SocialQuestionnaireParseResult Success(QuestionnaireSubmission submission) =>
        new(true, submission, null);

    public static SocialQuestionnaireParseResult Failure(string error) =>
        new(false, null, error);
}

/// <summary>
/// Strict JSON parser for QuestionnaireSubmission payloads.
/// Enforces payload size bounds, disallows duplicate JSON keys, rejects unknown properties,
/// prevents prototype pollution properties, and constructs an immutable QuestionnaireSubmission domain record.
/// </summary>
public sealed class SocialQuestionnaireSubmissionBodyParser
{
    public const int MaxPayloadSizeBytes = 32 * 1024; // 32 KB
    public const int MaxDepth = 6;
    public const int MaxIdLength = 128;

    private static readonly HashSet<string> DisallowedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__proto__",
        "prototype",
        "constructor"
    };

    public SocialQuestionnaireParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return SocialQuestionnaireParseResult.Failure("Payload cannot be null or empty.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxPayloadSizeBytes)
        {
            return SocialQuestionnaireParseResult.Failure($"Payload size {byteCount} bytes exceeds maximum limit of {MaxPayloadSizeBytes} bytes.");
        }

        var utf8Bytes = Encoding.UTF8.GetBytes(json);
        return Parse(utf8Bytes.AsSpan());
    }

    public SocialQuestionnaireParseResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            return SocialQuestionnaireParseResult.Failure("Payload cannot be null or empty.");
        }

        if (utf8Json.Length > MaxPayloadSizeBytes)
        {
            return SocialQuestionnaireParseResult.Failure($"Payload size {utf8Json.Length} bytes exceeds maximum limit of {MaxPayloadSizeBytes} bytes.");
        }

        try
        {
            var readerOptions = new JsonReaderOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            };

            var reader = new Utf8JsonReader(utf8Json, readerOptions);

            if (!reader.Read())
            {
                return SocialQuestionnaireParseResult.Failure("Unexpected end of JSON payload.");
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return SocialQuestionnaireParseResult.Failure("Root payload must be a JSON object.");
            }

            var rootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? version = null;
            bool? skip = null;
            List<QuestionnaireAnswer>? answers = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return SocialQuestionnaireParseResult.Failure("Expected JSON property name.");
                }

                var propName = reader.GetString();
                if (string.IsNullOrWhiteSpace(propName))
                {
                    return SocialQuestionnaireParseResult.Failure("Property name cannot be null or empty.");
                }

                if (DisallowedPropertyNames.Contains(propName))
                {
                    return SocialQuestionnaireParseResult.Failure($"Disallowed property '{propName}' detected.");
                }

                if (!rootKeys.Add(propName))
                {
                    return SocialQuestionnaireParseResult.Failure($"Duplicate property '{propName}' detected in root object.");
                }

                if (string.Equals(propName, "version", StringComparison.OrdinalIgnoreCase))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return SocialQuestionnaireParseResult.Failure("Property 'version' must be a string.");
                    }

                    var val = reader.GetString();
                    if (string.IsNullOrWhiteSpace(val) || val.Length > MaxIdLength)
                    {
                        return SocialQuestionnaireParseResult.Failure("Property 'version' must be a non-empty string within valid length bounds.");
                    }

                    version = val.Trim();
                }
                else if (string.Equals(propName, "skip", StringComparison.OrdinalIgnoreCase))
                {
                    reader.Read();
                    if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                    {
                        return SocialQuestionnaireParseResult.Failure("Property 'skip' must be a boolean value.");
                    }

                    skip = reader.GetBoolean();
                }
                else if (string.Equals(propName, "answers", StringComparison.OrdinalIgnoreCase))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        answers = [];
                        continue;
                    }

                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        return SocialQuestionnaireParseResult.Failure("Property 'answers' must be an array.");
                    }

                    answers = [];
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            break;
                        }

                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            return SocialQuestionnaireParseResult.Failure("Each element in 'answers' must be a JSON object.");
                        }

                        var answerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string? qId = null;
                        string? oId = null;

                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonTokenType.EndObject)
                            {
                                break;
                            }

                            if (reader.TokenType != JsonTokenType.PropertyName)
                            {
                                return SocialQuestionnaireParseResult.Failure("Expected property name in answer object.");
                            }

                            var aProp = reader.GetString();
                            if (string.IsNullOrWhiteSpace(aProp))
                            {
                                return SocialQuestionnaireParseResult.Failure("Property name in answer object cannot be empty.");
                            }

                            if (DisallowedPropertyNames.Contains(aProp))
                            {
                                return SocialQuestionnaireParseResult.Failure($"Disallowed property '{aProp}' detected in answer object.");
                            }

                            if (!answerKeys.Add(aProp))
                            {
                                return SocialQuestionnaireParseResult.Failure($"Duplicate property '{aProp}' detected in answer object.");
                            }

                            if (string.Equals(aProp, "questionId", StringComparison.OrdinalIgnoreCase))
                            {
                                reader.Read();
                                if (reader.TokenType != JsonTokenType.String)
                                {
                                    return SocialQuestionnaireParseResult.Failure("'questionId' must be a string.");
                                }

                                var val = reader.GetString();
                                if (string.IsNullOrWhiteSpace(val) || val.Length > MaxIdLength)
                                {
                                    return SocialQuestionnaireParseResult.Failure("'questionId' must be a non-empty string within valid bounds.");
                                }

                                qId = val.Trim();
                            }
                            else if (string.Equals(aProp, "optionId", StringComparison.OrdinalIgnoreCase))
                            {
                                reader.Read();
                                if (reader.TokenType != JsonTokenType.String)
                                {
                                    return SocialQuestionnaireParseResult.Failure("'optionId' must be a string.");
                                }

                                var val = reader.GetString();
                                if (string.IsNullOrWhiteSpace(val) || val.Length > MaxIdLength)
                                {
                                    return SocialQuestionnaireParseResult.Failure("'optionId' must be a non-empty string within valid bounds.");
                                }

                                oId = val.Trim();
                            }
                            else
                            {
                                return SocialQuestionnaireParseResult.Failure($"Unknown property '{aProp}' in answer object is not permitted.");
                            }
                        }

                        if (string.IsNullOrEmpty(qId))
                        {
                            return SocialQuestionnaireParseResult.Failure("Answer object is missing required 'questionId'.");
                        }

                        if (string.IsNullOrEmpty(oId))
                        {
                            return SocialQuestionnaireParseResult.Failure("Answer object is missing required 'optionId'.");
                        }

                        answers.Add(new QuestionnaireAnswer(qId, oId));
                    }
                }
                else
                {
                    return SocialQuestionnaireParseResult.Failure($"Unknown property '{propName}' is not permitted.");
                }
            }

            // Ensure no trailing tokens after root object
            if (reader.Read())
            {
                return SocialQuestionnaireParseResult.Failure("Unexpected trailing content after JSON root object.");
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                return SocialQuestionnaireParseResult.Failure("Missing required property 'version'.");
            }

            var isSkip = skip.GetValueOrDefault(false);
            var parsedAnswers = (IReadOnlyList<QuestionnaireAnswer>)(answers ?? (IReadOnlyList<QuestionnaireAnswer>)Array.Empty<QuestionnaireAnswer>());

            var submission = new QuestionnaireSubmission(
                version,
                parsedAnswers,
                isSkip);

            return SocialQuestionnaireParseResult.Success(submission);
        }
        catch (JsonException ex)
        {
            return SocialQuestionnaireParseResult.Failure($"Malformed JSON syntax: {ex.Message}");
        }
        catch (Exception ex)
        {
            return SocialQuestionnaireParseResult.Failure($"Invalid payload structure: {ex.Message}");
        }
    }
}
