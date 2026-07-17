using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;

namespace OCPP.Core.Server.Payments
{
    public sealed class InvoiceBuyerData
    {
        public string Country { get; set; }
        public string CompanyName { get; set; }
        public string Street { get; set; }
        public string PostalCode { get; set; }
        public string City { get; set; }
        public string Email { get; set; }
        public string TaxIdentifier { get; set; }
        public string RegistrationNumber { get; set; }
        public bool IdentifierIsVatRegistration { get; set; }
    }

    public sealed class InvoiceBuyerDataValidationResult
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string Field { get; set; }
        public string Error { get; set; }
        public InvoiceBuyerData Data { get; set; }
    }

    public sealed class InvoiceBuyerValidationException : ArgumentException
    {
        public InvoiceBuyerValidationException(string status, string field, string message)
            : base(message)
        {
            Status = status;
            Field = field;
        }

        public string Status { get; }
        public string Field { get; }
    }

    public static class InvoiceBuyerDataValidator
    {
        private static readonly HashSet<string> CountryCodes = new HashSet<string>(
            CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Select(culture =>
                {
                    try { return new RegionInfo(culture.Name).TwoLetterISORegionName; }
                    catch (ArgumentException) { return null; }
                })
                .Where(code => !string.IsNullOrWhiteSpace(code)),
            StringComparer.OrdinalIgnoreCase);

        public static InvoiceBuyerDataValidationResult ValidateAndNormalize(PaymentR1InvoiceRequest request)
        {
            if (request == null)
            {
                return Invalid("Invalid", null, "Invoice buyer data is required.");
            }

            if (!request.BuyerDataConfirmed)
            {
                return Invalid("ConfirmationRequired", "BuyerDataConfirmed", "Confirm that invoice data is correct before saving.");
            }

            var country = Trim(request.BuyerCountry);
            var taxIdentifier = Trim(request.BuyerTaxIdentifier) ?? Trim(request.BuyerOib);
            if (string.IsNullOrWhiteSpace(country) && !string.IsNullOrWhiteSpace(request.BuyerOib))
            {
                country = "HR";
            }
            country = country?.ToUpperInvariant();

            if (country?.Length != 2 || !country.All(char.IsLetter) || !CountryCodes.Contains(country))
            {
                return Invalid("InvalidCountry", "BuyerCountry", "Enter a valid two-letter country code.");
            }

            var fields = new[]
            {
                Field("BuyerCompanyName", request.BuyerCompanyName, 200, country != "HR"),
                Field("BuyerStreet", request.BuyerStreet, 200, country != "HR"),
                Field("BuyerPostalCode", request.BuyerPostalCode, 32, country != "HR"),
                Field("BuyerCity", request.BuyerCity, 100, country != "HR"),
                Field("BuyerEmail", request.BuyerEmail, 254, country != "HR"),
                Field("BuyerTaxIdentifier", taxIdentifier, 64, true),
                Field("BuyerRegistrationNumber", request.BuyerRegistrationNumber, 64, false)
            };

            foreach (var field in fields)
            {
                if (field.Error != null)
                {
                    return Invalid("InvalidBuyerData", field.Name, field.Error);
                }
            }

            var email = fields.Single(field => field.Name == "BuyerEmail").Value;
            if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
            {
                return Invalid("InvalidBuyerData", "BuyerEmail", "Enter a valid billing email address.");
            }

            if (string.Equals(country, "HR", StringComparison.OrdinalIgnoreCase) && !IsValidOib(taxIdentifier))
            {
                return Invalid("InvalidOib", "BuyerTaxIdentifier", "Valid OIB (11 digits) is required for a Croatian company invoice.");
            }

            return new InvoiceBuyerDataValidationResult
            {
                Success = true,
                Status = "Valid",
                Data = new InvoiceBuyerData
                {
                    Country = country,
                    CompanyName = fields.Single(field => field.Name == "BuyerCompanyName").Value,
                    Street = fields.Single(field => field.Name == "BuyerStreet").Value,
                    PostalCode = fields.Single(field => field.Name == "BuyerPostalCode").Value,
                    City = fields.Single(field => field.Name == "BuyerCity").Value,
                    Email = email,
                    TaxIdentifier = taxIdentifier,
                    RegistrationNumber = fields.Single(field => field.Name == "BuyerRegistrationNumber").Value,
                    IdentifierIsVatRegistration = request.BuyerIdentifierIsVatRegistration
                }
            };
        }

        public static InvoiceBuyerDataValidationResult ValidateAndNormalize(PaymentSessionRequest request)
        {
            return ValidateAndNormalize(request == null ? null : new PaymentR1InvoiceRequest
            {
                BuyerCompanyName = request.BuyerCompanyName,
                BuyerOib = request.BuyerOib,
                BuyerCountry = request.BuyerCountry,
                BuyerStreet = request.BuyerStreet,
                BuyerPostalCode = request.BuyerPostalCode,
                BuyerCity = request.BuyerCity,
                BuyerEmail = request.BuyerEmail,
                BuyerTaxIdentifier = request.BuyerTaxIdentifier,
                BuyerRegistrationNumber = request.BuyerRegistrationNumber,
                BuyerIdentifierIsVatRegistration = request.BuyerIdentifierIsVatRegistration,
                BuyerDataConfirmed = request.BuyerDataConfirmed
            });
        }

        private static (string Name, string Value, string Error) Field(string name, string value, int maxLength, bool required)
        {
            var normalized = Trim(value);
            if (required && string.IsNullOrWhiteSpace(normalized))
            {
                return (name, normalized, "This invoice field is required.");
            }
            if (normalized?.Length > maxLength)
            {
                return (name, normalized, $"This application accepts at most {maxLength} characters.");
            }
            if (normalized != null && normalized.Any(char.IsControl))
            {
                return (name, normalized, "Control characters and line breaks are not allowed.");
            }
            return (name, normalized, null);
        }

        private static string Trim(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static bool IsValidEmail(string value)
        {
            try { return string.Equals(new MailAddress(value).Address, value, StringComparison.OrdinalIgnoreCase); }
            catch (FormatException) { return false; }
        }

        private static bool IsValidOib(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 11 || value.Any(ch => ch < '0' || ch > '9')) return false;
            var remainder = 10;
            for (var index = 0; index < 10; index++)
            {
                remainder = (remainder + (value[index] - '0')) % 10;
                if (remainder == 0) remainder = 10;
                remainder = (remainder * 2) % 11;
            }
            var checkDigit = 11 - remainder;
            if (checkDigit == 10) checkDigit = 0;
            return checkDigit == value[10] - '0';
        }

        private static InvoiceBuyerDataValidationResult Invalid(string status, string field, string error) =>
            new InvoiceBuyerDataValidationResult { Success = false, Status = status, Field = field, Error = error };
    }
}
