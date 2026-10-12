using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

// Dalak patch: the whole of it is this file and the three lines DALAK.md lists.
//
// The flush made when the app goes to the background runs on a worker thread, not in a
// coroutine. A coroutine continues only when Unity's player loop runs, and the loop stops
// while the app is in the background: the request is sent and answered, but the events are
// deleted only when the app is next in the foreground. An app killed in the background
// therefore keeps events the server already has and sends them again on its next launch,
// with the same uuid, which the server stores as a second row.
//
// A worker thread deletes the events as soon as the server answers, so the events are at
// risk only for as long as the request takes.
namespace PostHogUnity
{
    partial class EventQueue
    {
        /// <summary>
        /// Flushes the queue when the app goes to the background.
        /// </summary>
        public void FlushOnBackground()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // No threads on WebGL.
            Flush();
#else
            if (!_isRunning)
            {
                PostHogLogger.Debug("EventQueue not running, skipping flush");
                return;
            }

            if (!TryBeginFlush())
            {
                PostHogLogger.Debug("Already flushing, skipping");
                return;
            }

            // Application.internetReachability can only be read on the main thread
            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                PostHogLogger.Debug("No network connectivity, skipping flush");
                EndFlush();
                return;
            }

            Task.Run(FlushBlocking);
#endif
        }

        /// <summary>
        /// Sends every batch in the queue on the calling thread, which is never the main
        /// one. The caller holds the flush (<see cref="TryBeginFlush"/>); it is released here.
        /// Mirrors <see cref="FlushCoroutine"/>, which is the reference for the handling of
        /// each result.
        /// </summary>
        internal void FlushBlocking()
        {
            try
            {
                // Check if paused due to errors
                if (_pausedUntil.HasValue && DateTime.UtcNow < _pausedUntil.Value)
                {
                    PostHogLogger.Debug($"Queue paused until {_pausedUntil.Value}");
                    return;
                }

                // Process all batches
                while (true)
                {
                    // Enqueue adds to the list behind GetEventIds under the same lock
                    List<string> batchIds;
                    lock (_lock)
                    {
                        var eventIds = _storage.GetEventIds();
                        int batchSize = Math.Min(eventIds.Count, _adjustedMaxBatchSize);
                        batchIds = new List<string>(batchSize);
                        for (int i = 0; i < batchSize; i++)
                        {
                            batchIds.Add(eventIds[i]);
                        }
                    }

                    if (batchIds.Count == 0)
                    {
                        break;
                    }

                    var events = LoadEvents(batchIds);

                    if (events.Count == 0)
                    {
                        break;
                    }

                    PostHogLogger.Debug($"Flushing batch of {events.Count} events");

                    var payload = new BatchPayload(_config.ApiKey, events);
                    bool success = _networkClient.SendBatchBlocking(payload, out int statusCode);

                    if (success)
                    {
                        // Delete successfully sent events
                        foreach (var id in batchIds)
                        {
                            _storage.DeleteEvent(id);
                        }

                        _retryCount = 0;
                        _pausedUntil = null;
                        PostHogLogger.Debug($"Successfully sent {events.Count} events");
                    }
                    else
                    {
                        if (ShouldDeleteEventsOnError(statusCode))
                        {
                            // Delete events on non-retryable errors
                            foreach (var id in batchIds)
                            {
                                _storage.DeleteEvent(id);
                            }
                        }
                        else
                        {
                            // Pause for retry
                            _retryCount++;
                            int delay = Math.Min(
                                _retryCount * RetryDelaySeconds,
                                MaxRetryDelaySeconds
                            );
                            _pausedUntil = DateTime.UtcNow.AddSeconds(delay);
                            PostHogLogger.Warning($"Flush failed, retrying in {delay}s");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Nothing observes an exception thrown on a thread pool thread
                PostHogLogger.Error("Background flush failed", ex);
            }
            finally
            {
                EndFlush();
            }
        }

        internal bool TryBeginFlush()
        {
            lock (_lock)
            {
                if (_isFlushing)
                {
                    return false;
                }
                _isFlushing = true;
                return true;
            }
        }

        void EndFlush()
        {
            lock (_lock)
            {
                _isFlushing = false;
            }
        }
    }

    partial class NetworkClient
    {
        // UnityWebRequest cannot be used off the main thread
        static readonly HttpClient BlockingHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

        /// <summary>
        /// Sends a batch of events to the PostHog API and waits for the answer. Never call
        /// it on the main thread.
        /// </summary>
        /// <param name="payload">The batch payload to send</param>
        /// <param name="statusCode">The HTTP status, 0 when there was no answer</param>
        /// <returns>True if the server accepted the batch</returns>
        public bool SendBatchBlocking(BatchPayload payload, out int statusCode)
        {
            statusCode = 0;
            var url = GetBatchUrl();
            var json = JsonSerializer.SerializeBatch(payload);

            PostHogLogger.Debug($"Sending batch to {url}");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", SdkInfo.UserAgent);

                using var response = BlockingHttpClient
                    .SendAsync(request)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                statusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    PostHogLogger.Debug($"Batch sent successfully (status: {statusCode})");
                    return true;
                }

                PostHogLogger.Warning($"Batch send failed (status: {statusCode})");
                return false;
            }
            catch (Exception ex)
            {
                PostHogLogger.Warning($"Batch send failed: {ex.Message} (status: 0)");
                return false;
            }
        }
    }
}
