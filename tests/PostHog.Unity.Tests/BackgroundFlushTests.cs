using System.Net;
using System.Net.Sockets;
using System.Text;
using PostHogUnity;

namespace PostHogUnity.Tests
{
    // Dalak patch (DALAK.md): the flush made when the app goes to the background.
    // Nothing here pumps a player loop, which is the point: the events are deleted by the
    // thread that got the answer.
    public class BackgroundFlushTests : IDisposable
    {
        readonly string _testBasePath;
        readonly FileStorageProvider _storage;
        readonly HttpListener _server;
        readonly string _host;
        readonly List<string> _requestBodies = new();
        readonly List<string> _requestPaths = new();
        readonly List<string> _userAgents = new();
        int _statusCode = 200;

        public BackgroundFlushTests()
        {
            _testBasePath = Path.Combine(Path.GetTempPath(), $"posthog-test-{Guid.NewGuid()}");
            _storage = new FileStorageProvider();
            _storage.Initialize(_testBasePath);

            var portFinder = new TcpListener(IPAddress.Loopback, 0);
            portFinder.Start();
            int port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
            portFinder.Stop();

            _host = $"http://localhost:{port}";
            _server = new HttpListener();
            _server.Prefixes.Add(_host + "/");
            _server.Start();
            _ = Task.Run(Serve);
        }

        public void Dispose()
        {
            try
            {
                _server.Close();
                if (Directory.Exists(_testBasePath))
                {
                    Directory.Delete(_testBasePath, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }

        void Serve()
        {
            while (_server.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _server.GetContext();
                }
                catch
                {
                    return;
                }

                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    lock (_requestBodies)
                    {
                        _requestBodies.Add(reader.ReadToEnd());
                        _requestPaths.Add(context.Request.Url.AbsolutePath);
                        _userAgents.Add(context.Request.UserAgent);
                    }
                }

                context.Response.StatusCode = _statusCode;
                context.Response.Close();
            }
        }

        EventQueue CreateQueue(int maxBatchSize = 50, string host = null)
        {
            var config = new PostHogConfig
            {
                ApiKey = "phc_test",
                Host = host ?? _host,
                MaxBatchSize = maxBatchSize,
            };
            return new EventQueue(config, _storage, new NetworkClient(config));
        }

        // The queue is never started, so Enqueue only stores: its own threshold flush
        // needs a coroutine runner.
        static List<string> EnqueueEvents(EventQueue queue, int count)
        {
            var uuids = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var evt = new PostHogEvent($"event_{i}", "player");
                uuids.Add(evt.Uuid);
                queue.Enqueue(evt);
            }
            return uuids;
        }

        static void Flush(EventQueue queue)
        {
            Assert.True(queue.TryBeginFlush());
            queue.FlushBlocking();
        }

        [Fact]
        public void DeletesTheEventsTheServerAccepted()
        {
            var queue = CreateQueue();
            var uuids = EnqueueEvents(queue, 3);

            Flush(queue);

            Assert.Equal(0, queue.Count);
            Assert.Empty(Directory.GetFiles(Path.Combine(_testBasePath, "queue")));
            var body = Assert.Single(_requestBodies);
            foreach (var uuid in uuids)
            {
                Assert.Contains(uuid, body);
            }
            Assert.Contains("\"api_key\":\"phc_test\"", body);
            Assert.Equal("/batch", _requestPaths[0]);
            Assert.Equal(SdkInfo.UserAgent, _userAgents[0]);
        }

        [Fact]
        public void SendsEveryBatchAndEachEventOnce()
        {
            var queue = CreateQueue(maxBatchSize: 2);
            var uuids = EnqueueEvents(queue, 5);

            Flush(queue);

            Assert.Equal(0, queue.Count);
            Assert.Equal(3, _requestBodies.Count);
            var everything = string.Concat(_requestBodies);
            foreach (var uuid in uuids)
            {
                Assert.Equal(1, CountOf(everything, uuid));
            }
        }

        [Fact]
        public void ReleasesTheFlushWhenItIsDone()
        {
            var queue = CreateQueue();
            EnqueueEvents(queue, 1);

            Assert.True(queue.TryBeginFlush());
            Assert.False(queue.TryBeginFlush());
            queue.FlushBlocking();

            Assert.True(queue.TryBeginFlush());
        }

        [Fact]
        public void KeepsTheEventsOnAServerError()
        {
            _statusCode = 500;
            var queue = CreateQueue();
            EnqueueEvents(queue, 2);

            Flush(queue);

            Assert.Equal(2, queue.Count);
            Assert.Single(_requestBodies);
        }

        [Fact]
        public void WaitsBeforeRetryingAfterAServerError()
        {
            _statusCode = 500;
            var queue = CreateQueue();
            EnqueueEvents(queue, 2);
            Flush(queue);

            _statusCode = 200;
            Flush(queue);

            Assert.Equal(2, queue.Count);
            Assert.Single(_requestBodies);
        }

        [Fact]
        public void DropsTheEventsOnAClientError()
        {
            _statusCode = 400;
            var queue = CreateQueue();
            EnqueueEvents(queue, 2);

            Flush(queue);

            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void KeepsTheEventsWhenTheServerCannotBeReached()
        {
            _server.Close();
            var queue = CreateQueue();
            EnqueueEvents(queue, 2);

            Flush(queue);

            Assert.Equal(2, queue.Count);
            Assert.True(queue.TryBeginFlush());
        }

        [Fact]
        public void DoesNothingWithAnEmptyQueue()
        {
            var queue = CreateQueue();

            Flush(queue);

            Assert.Empty(_requestBodies);
            Assert.True(queue.TryBeginFlush());
        }

        static int CountOf(string text, string value)
        {
            int count = 0;
            int index = text.IndexOf(value, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
            }
            return count;
        }
    }
}
