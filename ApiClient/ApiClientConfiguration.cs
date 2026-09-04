using System.Net;
using Polly;
using Polly.Retry;

namespace ACC.ApiClient;

public class ApiClientConfiguration : ApiClientOptions
{
    public ApiClientConfiguration(string clientId, string clientSecret, string accountId)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
        AccountId = accountId;
        RetryPolicy = GetRetryPolicy(RetryAttempts, InitialRetryInSeconds);
    }

    public string ClientId { get; }
    public string ClientSecret { get; }
    public string AccountId { get; }
    public AsyncRetryPolicy RetryPolicy { get; set; }

    public AsyncRetryPolicy GetRetryPolicy(int maxRetryAttempts, int initialRetryDelayInSeconds)
    {
        return Policy
            .Handle<Exception>(IsTransient)
            .WaitAndRetryAsync(
                retryCount: maxRetryAttempts,
                sleepDurationProvider: (retryAttempt, exception, context) =>
                {
                    // Check for 429 with Retry-After header
                    if (exception is HttpRequestException httpEx && 
                        httpEx.StatusCode == HttpStatusCode.TooManyRequests &&
                        httpEx.Data.Contains("RetryAfter"))
                    {
                        try 
                        {
                            var retryAfterSeconds = Convert.ToInt32(httpEx.Data["RetryAfter"]);
                            // Validate and cap at reasonable maximum (10 minutes)
                            retryAfterSeconds = Math.Min(Math.Max(1, retryAfterSeconds), 600);
                            // Add 1 second buffer as requested
                            return TimeSpan.FromSeconds(retryAfterSeconds + 1);
                        }
                        catch 
                        {
                            // Fall back to exponential backoff if conversion fails
                            Logger?.Debug("Failed to parse RetryAfter value, falling back to exponential backoff");
                        }
                    }
                    
                    // Default exponential backoff
                    return TimeSpan.FromSeconds(retryAttempt * initialRetryDelayInSeconds);
                },
                onRetryAsync: async (exception, timeSpan, retryCount, context) =>
                {
                    // Log the retry attempt
                    string message;
                    
                    if (exception is HttpRequestException httpEx && 
                        httpEx.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        if (httpEx.Data.Contains("RetryAfter"))
                        {
                            try
                            {
                                var retryAfterSeconds = Convert.ToInt32(httpEx.Data["RetryAfter"]);
                                message = $"Rate limit (429) error from Autodesk API. Server requested {retryAfterSeconds}s wait. " +
                                          $"Retry {retryCount}/{maxRetryAttempts} in {timeSpan.TotalSeconds:F0} seconds.";
                            }
                            catch
                            {
                                message = "Rate limit (429) error from Autodesk API (invalid Retry-After value). " +
                                          $"Retry {retryCount}/{maxRetryAttempts} in {timeSpan.TotalSeconds:F0} seconds.";
                            }
                        }
                        else
                        {
                            message = "Rate limit (429) error from Autodesk API (no Retry-After header). " +
                                      $"Retry {retryCount}/{maxRetryAttempts} in {timeSpan.TotalSeconds:F0} seconds.";
                        }
                    }
                    else
                    {
                        message = "Error communicating with Autodesk API. " +
                                  "Expecting this to be a transient error. " +
                                  $"Retry {retryCount}/{maxRetryAttempts} in {timeSpan.TotalSeconds:F0} seconds.";
                    }
                    
                    Logger?.Warn(exception, message);
                    await Task.CompletedTask;
                });
    }

    // Only transient failures are worth retrying. A 4xx answer (403 Forbidden, 404 Not Found,
    // 409 Conflict, ...) describes the request itself and will say the same thing on every
    // attempt, so retrying it only burns the backoff budget before failing anyway.
    // 401 stays retryable: HandleUnsuccessfulStatusCode clears the cached token on the way out,
    // so the next attempt re-authenticates.
    private static bool IsTransient(Exception exception)
    {
        if (exception is not HttpRequestException httpEx) return true;
        // No status code means the request never got an answer (DNS, socket, TLS, timeout).
        if (httpEx.StatusCode is null) return true;
        return (int)httpEx.StatusCode >= 500 ||
               httpEx.StatusCode is HttpStatusCode.Unauthorized
                   or HttpStatusCode.RequestTimeout
                   or HttpStatusCode.TooManyRequests;
    }
}
