using OCPP.Core.Server.Payments;
using Xunit;

namespace OCPP.Core.Server.Tests
{
    public class InvoiceBuyerDataValidatorTests
    {
        [Fact]
        public void ValidateAndNormalize_AcceptsCroatianBuyerWithValidOib()
        {
            var result = InvoiceBuyerDataValidator.ValidateAndNormalize(new PaymentR1InvoiceRequest
            {
                BuyerCountry = " hr ",
                BuyerCompanyName = " Example d.o.o. ",
                BuyerStreet = " Ulica 1 ",
                BuyerPostalCode = " 10000 ",
                BuyerCity = " Zagreb ",
                BuyerEmail = " billing@example.com ",
                BuyerTaxIdentifier = " 12345678903 ",
                BuyerIdentifierIsVatRegistration = true,
                BuyerDataConfirmed = true
            });

            Assert.True(result.Success);
            Assert.Equal("HR", result.Data.Country);
            Assert.Equal("Example d.o.o.", result.Data.CompanyName);
            Assert.Equal("12345678903", result.Data.TaxIdentifier);
            Assert.True(result.Data.IdentifierIsVatRegistration);
        }

        [Fact]
        public void ValidateAndNormalize_AcceptsUnverifiedForeignIdentifierWithoutVies()
        {
            var result = InvoiceBuyerDataValidator.ValidateAndNormalize(new PaymentR1InvoiceRequest
            {
                BuyerCountry = "cz",
                BuyerCompanyName = "Example s.r.o.",
                BuyerStreet = "Pražská 1",
                BuyerPostalCode = "110 00",
                BuyerCity = "Praha",
                BuyerEmail = "billing@example.cz",
                BuyerTaxIdentifier = " CZ 123-ABC ",
                BuyerRegistrationNumber = "C 12345",
                BuyerIdentifierIsVatRegistration = false,
                BuyerDataConfirmed = true
            });

            Assert.True(result.Success);
            Assert.Equal("CZ 123-ABC", result.Data.TaxIdentifier);
            Assert.Equal("C 12345", result.Data.RegistrationNumber);
            Assert.False(result.Data.IdentifierIsVatRegistration);
        }

        [Fact]
        public void ValidateAndNormalize_PaymentSessionRequest_UsesForeignBuyerContract()
        {
            var result = InvoiceBuyerDataValidator.ValidateAndNormalize(new PaymentSessionRequest
            {
                RequestR1Invoice = true,
                BuyerCountry = "cz",
                BuyerCompanyName = " Example s.r.o. ",
                BuyerStreet = " Pražská 1 ",
                BuyerPostalCode = "110 00",
                BuyerCity = "Praha",
                BuyerEmail = "billing@example.cz",
                BuyerTaxIdentifier = "CZ 123-ABC",
                BuyerRegistrationNumber = "C 12345",
                BuyerIdentifierIsVatRegistration = true,
                BuyerDataConfirmed = true
            });

            Assert.True(result.Success);
            Assert.Equal("CZ", result.Data.Country);
            Assert.Equal("Example s.r.o.", result.Data.CompanyName);
            Assert.Equal("Pražská 1", result.Data.Street);
            Assert.Equal("CZ 123-ABC", result.Data.TaxIdentifier);
            Assert.True(result.Data.IdentifierIsVatRegistration);
        }

        [Theory]
        [InlineData("Company\nName", "BuyerCompanyName")]
        [InlineData("Street\rName", "BuyerStreet")]
        [InlineData("VAT\t123", "BuyerTaxIdentifier")]
        public void ValidateAndNormalize_RejectsControlCharacters(string value, string expectedField)
        {
            var request = ValidForeignRequest();
            if (expectedField == "BuyerCompanyName") request.BuyerCompanyName = value;
            if (expectedField == "BuyerStreet") request.BuyerStreet = value;
            if (expectedField == "BuyerTaxIdentifier") request.BuyerTaxIdentifier = value;

            var result = InvoiceBuyerDataValidator.ValidateAndNormalize(request);

            Assert.False(result.Success);
            Assert.Equal(expectedField, result.Field);
        }

        [Fact]
        public void ValidateAndNormalize_RejectsForeignIdentifierPastApplicationLimit()
        {
            var request = ValidForeignRequest();
            request.BuyerTaxIdentifier = new string('X', 65);

            var result = InvoiceBuyerDataValidator.ValidateAndNormalize(request);

            Assert.False(result.Success);
            Assert.Equal("BuyerTaxIdentifier", result.Field);
        }

        [Fact]
        public void ValidateAndNormalize_RequiresExplicitConfirmation()
        {
            var request = ValidForeignRequest();
            request.BuyerDataConfirmed = false;

            var result = InvoiceBuyerDataValidator.ValidateAndNormalize(request);

            Assert.False(result.Success);
            Assert.Equal("BuyerDataConfirmed", result.Field);
        }

        [Fact]
        public void ValidateAndNormalize_RejectsInvalidCroatianOib()
        {
            var request = ValidForeignRequest();
            request.BuyerCountry = "HR";
            request.BuyerTaxIdentifier = "12345678901";

            var result = InvoiceBuyerDataValidator.ValidateAndNormalize(request);

            Assert.False(result.Success);
            Assert.Equal("InvalidOib", result.Status);
        }

        private static PaymentR1InvoiceRequest ValidForeignRequest() => new PaymentR1InvoiceRequest
        {
            BuyerCountry = "CZ",
            BuyerCompanyName = "Example s.r.o.",
            BuyerStreet = "Pražská 1",
            BuyerPostalCode = "110 00",
            BuyerCity = "Praha",
            BuyerEmail = "billing@example.cz",
            BuyerTaxIdentifier = "CZ12345678",
            BuyerDataConfirmed = true
        };
    }
}
