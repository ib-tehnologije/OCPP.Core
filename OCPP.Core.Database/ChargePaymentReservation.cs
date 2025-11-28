/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2025 dallmann consulting GmbH.
 * All Rights Reserved.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;

#nullable disable

namespace OCPP.Core.Database
{
    public partial class ChargePaymentReservation
    {
        public Guid ReservationId { get; set; }
        public string ChargePointId { get; set; }
        public int ConnectorId { get; set; }
        public string ChargeTagId { get; set; }
        public double MaxEnergyKwh { get; set; }
        public decimal PricePerKwh { get; set; }
        public decimal UserSessionFee { get; set; }
        public decimal OwnerSessionFee { get; set; }
        public decimal OwnerCommissionPercent { get; set; }
        public decimal OwnerCommissionFixedPerKwh { get; set; }
        public long MaxAmountCents { get; set; }
        public decimal UsageFeePerMinute { get; set; }
        public int StartUsageFeeAfterMinutes { get; set; }
        public int MaxUsageFeeMinutes { get; set; }
        public string Currency { get; set; }
        public string StripeCheckoutSessionId { get; set; }
        public string StripePaymentIntentId { get; set; }
        public string Status { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? AuthorizedAtUtc { get; set; }
        public DateTime? CapturedAtUtc { get; set; }
        public int? TransactionId { get; set; }
        public long? CapturedAmountCents { get; set; }
        public double? ActualEnergyKwh { get; set; }
        public string LastError { get; set; }
    }
}
