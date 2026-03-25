using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Stripe;
using Stripe.Checkout;

namespace OCPP.Core.Server.Payments
{
    internal sealed class MockStripeStore
    {
        public ConcurrentDictionary<string, Session> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, PaymentIntent> PaymentIntents { get; } = new(StringComparer.OrdinalIgnoreCase);
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
            return CloneIntent(intent);
        }

        public void Cancel(string id, RequestOptions requestOptions = null)
        {
            if (_store.PaymentIntents.TryGetValue(id ?? string.Empty, out var intent))
            {
                intent.Status = "canceled";
                _store.PaymentIntents[id] = intent;
            }
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
                AmountReceived = intent.AmountReceived,
                Metadata = intent.Metadata == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(intent.Metadata, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
