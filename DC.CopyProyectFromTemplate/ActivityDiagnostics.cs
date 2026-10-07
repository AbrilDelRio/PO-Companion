using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;

// Root namespace on purpose: both Functions and Services need it.
namespace DC.CopyProyectFromTemplate;

internal static class ActivityDiagnostics
{
    private static readonly Regex AadstsPattern =
        new Regex(@"AADSTS\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Flattens the whole exception chain into one line. The Functions host only records its own
    /// FunctionInvocationException wrapper, so the worker's real exception never reaches the log
    /// stream unless it is written into the message itself.
    /// </summary>
    public static string Flatten(Exception exception)
    {
        StringBuilder builder = new StringBuilder();
        Exception? current = exception;
        int depth = 0;

        while (current != null && depth < 10)
        {
            if (depth > 0)
            {
                builder.Append(" --> ");
            }

            builder.Append(current.GetType().FullName).Append(": ").Append(current.Message);

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    builder.Append(" [+] ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                }
            }

            current = current.InnerException;
            depth++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// First AADSTS code anywhere in the exception chain, including the raw response body MSAL
    /// keeps on service exceptions. Null when Entra did not answer with one.
    /// </summary>
    public static string? FindAadstsCode(Exception exception)
    {
        Exception? current = exception;
        int depth = 0;

        while (current != null && depth < 10)
        {
            Match match = AadstsPattern.Match(current.Message ?? string.Empty);
            if (match.Success)
            {
                return match.Value;
            }

            if (current is MsalServiceException service && !string.IsNullOrEmpty(service.ResponseBody))
            {
                match = AadstsPattern.Match(service.ResponseBody);
                if (match.Success)
                {
                    return match.Value;
                }
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    string? code = FindAadstsCode(inner);
                    if (code != null)
                    {
                        return code;
                    }
                }
            }

            current = current.InnerException;
            depth++;
        }

        return null;
    }
}
