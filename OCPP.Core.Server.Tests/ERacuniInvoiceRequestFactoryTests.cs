using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OCPP.Core.Server.Payments.Invoices;
using OCPP.Core.Server.Payments.Invoices.ERacuni;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class ERacuniInvoiceRequestFactoryTests
    {
        [Fact]
        public void BuildCreateSalesInvoiceRequest_MapsRetailPayload_FromInvoiceDraft()
        {
            var factory = CreateFactory();
            var reservationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var draft = new InvoiceDraft
            {
                ReservationId = reservationId,
                TransactionId = 42,
                InvoiceKind = "R1",
                IssueDateUtc = new DateTime(2026, 3, 5, 14, 15, 0, DateTimeKind.Utc),
                ServiceDateFromUtc = new DateTime(2026, 3, 5, 13, 0, 0, DateTimeKind.Utc),
                ServiceDateToUtc = new DateTime(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc),
                Currency = "EUR",
                BuyerCompanyName = "Acme d.o.o.",
                BuyerOib = "12345678901",
                BuyerEmail = "billing@example.com",
                ChargePointId = "CP-42",
                ConnectorId = 2,
                StripePaymentIntentId = "pi_123",
                StripeCheckoutSessionId = "cs_123",
                Lines = new List<InvoiceDraftLine>
                {
                    new InvoiceDraftLine
                    {
                        Type = "Energy",
                        Description = "Charging energy",
                        Quantity = 12.5m,
                        UnitCode = "kWh",
                        UnitPrice = 0.30m,
                        LineAmount = 3.75m
                    },
                    new InvoiceDraftLine
                    {
                        Type = "IdleFee",
                        Description = "Idle / occupancy fee",
                        Quantity = 15m,
                        UnitCode = "MIN",
                        UnitPrice = 0.20m,
                        LineAmount = 3.00m
                    }
                }
            };

            var request = factory.BuildCreateSalesInvoiceRequest(draft);
            var parameters = Assert.IsType<ERacuniSalesInvoiceCreateParameters>(request.Parameters);

            Assert.Equal("api-user", request.Username);
            Assert.Equal("secret-1234", request.SecretKey);
            Assert.Equal("token-9876", request.Token);
            Assert.Equal("SalesInvoiceCreate", request.Method);
            Assert.Equal("11111111222233334444555555555555", parameters.ApiTransactionId);
            Assert.True(parameters.GeneratePublicUrl);
            Assert.False(parameters.SendIssuedInvoiceByEmail);

            var invoice = parameters.SalesInvoice;
            Assert.Equal("IssuedInvoice", invoice.Status);
            Assert.Equal("Zagreb", invoice.City);
            Assert.Equal(2026, invoice.BusinessYear);
            Assert.Equal("POS1", invoice.BusinessUnit);
            Assert.Equal("WH1", invoice.WarehouseCode);
            Assert.Equal("CR1", invoice.CashRegisterCode);
            Assert.Equal("EUR", invoice.DocumentCurrency);
            Assert.Equal("2026-03-05", invoice.Date);
            Assert.Equal("2026-03-05", invoice.PaymentDueDate);
            Assert.Equal("2026-03-05", invoice.DateOfSupplyFrom);
            Assert.Equal("2026-03-05", invoice.DateOfSupplyUntil);
            Assert.Equal("Croatian", invoice.DocumentLanguage);
            Assert.Null(invoice.MethodOfPayment);
            Assert.Equal("Retail", invoice.Type);
            Assert.Equal("Acme d.o.o.", invoice.BuyerName);
            Assert.Equal("12345678901", invoice.BuyerTaxNumber);
            Assert.Null(invoice.BuyerCode);
            Assert.Equal("Registered", invoice.BuyerVatRegistration);
            Assert.Equal("billing@example.com", invoice.BuyerEMail);
            Assert.Equal("STRIPE-pi_123", invoice.Reference);
            Assert.Equal("EVSE-42", invoice.OrderReference);
            Assert.Contains("CP-42", invoice.Remarks);
            Assert.Contains("reservation", invoice.Remarks, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, invoice.Items.Count);

            var energy = invoice.Items[0];
            Assert.Equal("EV-ENERGY", energy.ProductCode);
            Assert.Equal("Charging energy", energy.Description);
            Assert.Equal(12.5m, energy.Quantity);
            Assert.Equal("kWh", energy.Unit);
            Assert.Equal("EUR", energy.Currency);
            Assert.Equal(0.30m, energy.Price);
            Assert.Null(energy.NetPrice);
            Assert.Equal(25m, energy.VatPercentage);
            Assert.Equal("0", energy.VatTransactionType);

            var idle = invoice.Items[1];
            Assert.Equal("EV-IDLE", idle.ProductCode);
            Assert.Equal("MIN", idle.Unit);
            Assert.Equal(0.20m, idle.Price);
        }

        [Fact]
        public void BuildCreateSalesInvoiceRequest_ThrowsWhenR1BuyerTaxNumberIsMissing()
        {
            var factory = CreateFactory();
            var draft = new InvoiceDraft
            {
                ReservationId = Guid.NewGuid(),
                TransactionId = 7,
                InvoiceKind = "R1",
                IssueDateUtc = DateTime.UtcNow,
                ServiceDateFromUtc = DateTime.UtcNow,
                Currency = "EUR",
                BuyerCompanyName = "Buyer d.o.o.",
                Lines = new List<InvoiceDraftLine>
                {
                    new InvoiceDraftLine
                    {
                        Type = "Energy",
                        Description = "Charging energy",
                        Quantity = 2m,
                        UnitCode = "kWh",
                        UnitPrice = 0.30m,
                        LineAmount = 0.60m
                    }
                }
            };

            var ex = Assert.Throws<InvalidOperationException>(() => factory.BuildCreateSalesInvoiceRequest(draft));
            Assert.Contains("buyer OIB", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildSanitizedLogPayload_MasksSecretValues()
        {
            var factory = CreateFactory();
            var request = new ERacuniApiRequestEnvelope
            {
                Username = "api-user",
                SecretKey = "secret-1234",
                Token = "token-9876",
                Method = "SalesInvoiceCreate",
                Parameters = new { apiTransactionId = "tx-1" }
            };

            var payload = Assert.IsType<JObject>(factory.BuildSanitizedLogPayload(request));

            Assert.Equal("api-user", payload["username"]?.Value<string>());
            Assert.Equal("se***34", payload["secretKey"]?.Value<string>());
            Assert.Equal("to***76", payload["token"]?.Value<string>());
        }

        [Fact]
        public void BuildCreateSalesInvoiceRequest_MapsOptionalBuyerCodeAndMethodOfPayment_WhenConfigured()
        {
            var factory = CreateFactory(eracuni =>
            {
                eracuni.MethodOfPayment = "CreditCard";
            });

            var draft = new InvoiceDraft
            {
                ReservationId = Guid.NewGuid(),
                TransactionId = 55,
                InvoiceKind = "Retail",
                IssueDateUtc = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
                ServiceDateFromUtc = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
                Currency = "EUR",
                BuyerCompanyName = "Known buyer d.o.o.",
                BuyerCode = "BUYER-001",
                Lines = new List<InvoiceDraftLine>
                {
                    new InvoiceDraftLine
                    {
                        Type = "Energy",
                        Description = "Charging energy",
                        Quantity = 1m,
                        UnitCode = "kWh",
                        UnitPrice = 0.30m,
                        LineAmount = 0.30m
                    }
                }
            };

            var request = factory.BuildCreateSalesInvoiceRequest(draft);
            var parameters = Assert.IsType<ERacuniSalesInvoiceCreateParameters>(request.Parameters);

            Assert.Equal("BUYER-001", parameters.SalesInvoice.BuyerCode);
            Assert.Equal("CreditCard", parameters.SalesInvoice.MethodOfPayment);
        }

        private static ERacuniInvoiceRequestFactory CreateFactory(Action<ERacuniInvoiceOptions>? configure = null)
        {
            var eracuni = new ERacuniInvoiceOptions
            {
                Username = "api-user",
                SecretKey = "secret-1234",
                Token = "token-9876",
                City = "Zagreb",
                BusinessUnit = "POS1",
                WarehouseCode = "WH1",
                CashRegisterCode = "CR1",
                DocumentLanguage = "Croatian",
                DocumentType = "Retail",
                R1DocumentType = "Retail",
                DefaultVatPercentage = 25m,
                VatTransactionType = "0",
                ReferencePrefix = "STRIPE",
                OrderReferencePrefix = "EVSE",
                GeneratePublicUrl = true,
                TimeZoneId = "Europe/Zagreb",
                RequireBuyerTaxNumberForR1 = true,
                LineItems = new Dictionary<string, ERacuniLineItemOptions>
                {
                    ["Energy"] = new ERacuniLineItemOptions { ProductCode = "EV-ENERGY", Unit = "kWh" },
                    ["IdleFee"] = new ERacuniLineItemOptions { ProductCode = "EV-IDLE", Unit = "MIN" }
                }
            };

            configure?.Invoke(eracuni);

            return new ERacuniInvoiceRequestFactory(Options.Create(new InvoiceIntegrationOptions
            {
                ERacuni = eracuni
            }));
        }
    }
}
