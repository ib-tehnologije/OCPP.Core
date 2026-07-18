using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    internal sealed class MockStripeStore
    {
        private readonly object _snapshotLock = new();
        private readonly string _snapshotFilePath;

        public MockStripeStore(IConfiguration configuration)
        {
            string diagnosticsDirectory = configuration?.GetValue<string>("Stripe:MockDiagnosticsDirectory");
            if (!string.IsNullOrWhiteSpace(diagnosticsDirectory))
            {
                try
                {
                    Directory.CreateDirectory(diagnosticsDirectory);
                    _snapshotFilePath = Path.Combine(diagnosticsDirectory, "mock-stripe-store.json");
                    RestoreSnapshot();
                }
                catch (Exception exception) when (IsOptionalSnapshotFailure(exception))
                {
                    _snapshotFilePath = null;
                }
            }
        }

        public ConcurrentDictionary<string, Session> Sessions { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, PaymentIntent> PaymentIntents { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        private void RestoreSnapshot()
        {
            if (string.IsNullOrWhiteSpace(_snapshotFilePath) || !System.IO.File.Exists(_snapshotFilePath))
            {
                return;
            }

            try
            {
                var snapshot = JsonSerializer.Deserialize<MockStripeSnapshot>(
                    System.IO.File.ReadAllText(_snapshotFilePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (snapshot?.Sessions == null || snapshot.PaymentIntents == null)
                {
                    throw new JsonException("Mock Stripe snapshot must contain session and payment intent arrays.");
                }

                var restoredSessions = new ConcurrentDictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
                var restoredPaymentIntents = new ConcurrentDictionary<string, PaymentIntent>(StringComparer.OrdinalIgnoreCase);

                foreach (var session in snapshot.Sessions)
                {
                    if (session == null || string.IsNullOrWhiteSpace(session.Id))
                    {
                        throw new JsonException("Mock Stripe session entries must have an id.");
                    }

                    var restoredSession = new Session
                    {
                        Id = session.Id,
                        Url = session.Url,
                        PaymentIntentId = session.PaymentIntentId,
                        Status = session.Status,
                        PaymentStatus = session.PaymentStatus,
                        Metadata = RestoreMetadata(session.Metadata)
                    };
                    if (!restoredSessions.TryAdd(session.Id, restoredSession))
                    {
                        throw new JsonException($"Duplicate mock Stripe session id '{session.Id}'.");
                    }
                }

                foreach (var intent in snapshot.PaymentIntents)
                {
                    if (intent == null || string.IsNullOrWhiteSpace(intent.Id))
                    {
                        throw new JsonException("Mock Stripe payment intent entries must have an id.");
                    }

                    var restoredIntent = new PaymentIntent
                    {
                        Id = intent.Id,
                        Status = intent.Status,
                        Amount = intent.Amount,
                        AmountReceived = intent.AmountReceived,
                        Metadata = RestoreMetadata(intent.Metadata)
                    };
                    if (!restoredPaymentIntents.TryAdd(intent.Id, restoredIntent))
                    {
                        throw new JsonException($"Duplicate mock Stripe payment intent id '{intent.Id}'.");
                    }
                }

                Sessions = restoredSessions;
                PaymentIntents = restoredPaymentIntents;
            }
            catch (Exception exception) when (IsOptionalSnapshotFailure(exception))
            {
                // Diagnostics are optional local mock state. Publish nothing unless the full snapshot is valid.
            }
        }

        private static Dictionary<string, string> RestoreMetadata(Dictionary<string, string> metadata)
        {
            if (metadata == null)
            {
                throw new JsonException("Mock Stripe metadata must be an object.");
            }

            var restored = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
            if (restored.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null))
            {
                throw new JsonException("Mock Stripe metadata keys and values must be non-empty strings.");
            }

            return restored;
        }

        private static bool IsOptionalSnapshotFailure(Exception exception) =>
            exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

        public void PersistSnapshot()
        {
            if (string.IsNullOrWhiteSpace(_snapshotFilePath))
            {
                return;
            }

            var snapshot = new
            {
                generatedAtUtc = DateTime.UtcNow,
                sessions = Sessions.Values
                    .OrderBy(session => session?.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(session => new
                    {
                        id = session?.Id,
                        url = session?.Url,
                        paymentIntentId = session?.PaymentIntentId,
                        status = session?.Status,
                        paymentStatus = session?.PaymentStatus,
                        metadata = session?.Metadata == null
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(session.Metadata, StringComparer.OrdinalIgnoreCase)
                    })
                    .ToList(),
                paymentIntents = PaymentIntents.Values
                    .OrderBy(intent => intent?.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(intent => new
                    {
                        id = intent?.Id,
                        status = intent?.Status,
                        amount = intent?.Amount,
                        amountReceived = intent?.AmountReceived,
                        metadata = intent?.Metadata == null
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(intent.Metadata, StringComparer.OrdinalIgnoreCase)
                    })
                    .ToList()
            };

            string tempFilePath = $"{_snapshotFilePath}.tmp";
            lock (_snapshotLock)
            {
                System.IO.File.WriteAllText(
                    tempFilePath,
                    JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));

                System.IO.File.Move(tempFilePath, _snapshotFilePath, true);
            }
        }

        private sealed class MockStripeSnapshot
        {
            public List<MockStripeSessionSnapshot> Sessions { get; set; }
            public List<MockStripePaymentIntentSnapshot> PaymentIntents { get; set; }
        }

        private sealed class MockStripeSessionSnapshot
        {
            public string Id { get; set; }
            public string Url { get; set; }
            public string PaymentIntentId { get; set; }
            public string Status { get; set; }
            public string PaymentStatus { get; set; }
            public Dictionary<string, string> Metadata { get; set; }
        }

        private sealed class MockStripePaymentIntentSnapshot
        {
            public string Id { get; set; }
            public string Status { get; set; }
            public long Amount { get; set; }
            public long AmountReceived { get; set; }
            public Dictionary<string, string> Metadata { get; set; }
        }
    }

    internal sealed class MockStripeSessionService : IStripeSessionService
    {
        private readonly MockStripeStore _store;
        private readonly IConfiguration _configuration;

        public MockStripeSessionService(MockStripeStore store, IConfiguration configuration)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _configuration = configuration;
        }

        public Session Create(SessionCreateOptions options, RequestOptions requestOptions = null)
        {
            string sessionId = $"mock_sess_{Guid.NewGuid():N}";
            string paymentIntentId = $"mock_pi_{Guid.NewGuid():N}";
            long amount = ResolveAmount(options);
            string successUrl = ReplaceCheckoutSessionId(options?.SuccessUrl, sessionId);
            string cancelUrl = ReplaceCheckoutSessionId(options?.CancelUrl, sessionId);
            string managementBaseUrl = ResolveManagementBaseUrl(successUrl, cancelUrl);
            string mockCustomerEmail = _configuration?.GetValue<string>("Stripe:MockCustomerEmail");
            if (string.IsNullOrWhiteSpace(mockCustomerEmail))
            {
                mockCustomerEmail = "driver@example.test";
            }

            var session = new Session
            {
                Id = sessionId,
                Url = $"{managementBaseUrl}/Payments/MockCheckout?reservationId={Uri.EscapeDataString(options?.Metadata?["reservation_id"] ?? string.Empty)}&session_id={Uri.EscapeDataString(sessionId)}&successUrl={Uri.EscapeDataString(successUrl)}&cancelUrl={Uri.EscapeDataString(cancelUrl)}",
                PaymentIntentId = paymentIntentId,
                Status = "complete",
                PaymentStatus = "paid",
                Metadata = CloneDictionary(options?.Metadata),
                CustomerDetails = new SessionCustomerDetails
                {
                    Email = mockCustomerEmail
                }
            };

            _store.Sessions[sessionId] = session;
            _store.PaymentIntents[paymentIntentId] = new PaymentIntent
            {
                Id = paymentIntentId,
                Status = "requires_capture",
                Amount = amount,
                Metadata = CloneDictionary(options?.PaymentIntentData?.Metadata ?? options?.Metadata)
            };
            _store.PersistSnapshot();

            return CloneSession(session);
        }

        public Session Get(string id)
        {
            return _store.Sessions.TryGetValue(id ?? string.Empty, out var session)
                ? CloneSession(session)
                : null;
        }

        public Session Update(string id, SessionUpdateOptions options, RequestOptions requestOptions = null)
        {
            if (!_store.Sessions.TryGetValue(id ?? string.Empty, out var session))
            {
                return null;
            }

            if (options?.Metadata != null)
            {
                session.Metadata = CloneDictionary(options.Metadata);
            }

            _store.Sessions[id] = session;
            _store.PersistSnapshot();
            return CloneSession(session);
        }

        private static string ResolveManagementBaseUrl(string successUrl, string cancelUrl)
        {
            if (Uri.TryCreate(successUrl, UriKind.Absolute, out var successUri))
            {
                return successUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }

            if (Uri.TryCreate(cancelUrl, UriKind.Absolute, out var cancelUri))
            {
                return cancelUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            }

            return "http://127.0.0.1:8082";
        }

        private static string ReplaceCheckoutSessionId(string url, string sessionId)
        {
            return string.IsNullOrWhiteSpace(url)
                ? string.Empty
                : url.Replace("{CHECKOUT_SESSION_ID}", Uri.EscapeDataString(sessionId), StringComparison.Ordinal);
        }

        private static long ResolveAmount(SessionCreateOptions options)
        {
            if (options?.LineItems == null)
            {
                return 0;
            }

            return options.LineItems.Sum(item =>
            {
                long quantity = item?.Quantity ?? 1;
                long unitAmount = item?.PriceData?.UnitAmount ?? 0;
                return quantity * unitAmount;
            });
        }

        private static Session CloneSession(Session session)
        {
            if (session == null)
            {
                return null;
            }

            return new Session
            {
                Id = session.Id,
                Url = session.Url,
                PaymentIntentId = session.PaymentIntentId,
                Status = session.Status,
                PaymentStatus = session.PaymentStatus,
                Metadata = CloneDictionary(session.Metadata),
                CustomerDetails = session.CustomerDetails == null
                    ? null
                    : new SessionCustomerDetails
                    {
                        Email = session.CustomerDetails.Email,
                        Name = session.CustomerDetails.Name
                    }
            };
        }

        private static Dictionary<string, string> CloneDictionary(IDictionary<string, string> source)
        {
            return source == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal sealed class MockStripePaymentIntentService : IStripePaymentIntentService
    {
        private readonly MockStripeStore _store;

        public MockStripePaymentIntentService(MockStripeStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public PaymentIntent Get(string id)
        {
            return _store.PaymentIntents.TryGetValue(id ?? string.Empty, out var intent)
                ? CloneIntent(intent)
                : null;
        }

        public PaymentIntent Update(string id, PaymentIntentUpdateOptions options, RequestOptions requestOptions = null)
        {
            if (!_store.PaymentIntents.TryGetValue(id ?? string.Empty, out var intent))
            {
                return null;
            }

            if (options?.Metadata != null)
            {
                intent.Metadata = new Dictionary<string, string>(options.Metadata, StringComparer.OrdinalIgnoreCase);
            }

            _store.PaymentIntents[id] = intent;
            _store.PersistSnapshot();
            return CloneIntent(intent);
        }

        public PaymentIntent Capture(string id, PaymentIntentCaptureOptions options, RequestOptions requestOptions = null)
        {
            if (!_store.PaymentIntents.TryGetValue(id ?? string.Empty, out var intent))
            {
                return null;
            }

            intent.Status = "succeeded";
            intent.AmountReceived = options?.AmountToCapture ?? intent.Amount;
            _store.PaymentIntents[id] = intent;
            _store.PersistSnapshot();
            return CloneIntent(intent);
        }

        public PaymentIntent Cancel(string id, RequestOptions requestOptions = null)
        {
            if (_store.PaymentIntents.TryGetValue(id ?? string.Empty, out var intent))
            {
                intent.Status = "canceled";
                intent.AmountCapturable = 0;
                _store.PaymentIntents[id] = intent;
                _store.PersistSnapshot();
                return CloneIntent(intent);
            }

            return null;
        }

        private static PaymentIntent CloneIntent(PaymentIntent intent)
        {
            if (intent == null)
            {
                return null;
            }

            return new PaymentIntent
            {
                Id = intent.Id,
                Status = intent.Status,
                Amount = intent.Amount,
                AmountCapturable = intent.AmountCapturable,
                AmountReceived = intent.AmountReceived,
                Metadata = intent.Metadata == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(intent.Metadata, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
