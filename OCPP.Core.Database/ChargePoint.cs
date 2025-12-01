/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2021 dallmann consulting GmbH.
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
using System.Collections.Generic;

#nullable disable

namespace OCPP.Core.Database
{
    public partial class ChargePoint
    {
        public ChargePoint()
        {
            Transactions = new HashSet<Transaction>();
        }

        public string ChargePointId { get; set; }
        public string Name { get; set; }
        public string Comment { get; set; }
        public string Description { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string ClientCertThumb { get; set; }
        public bool FreeChargingEnabled { get; set; }
        public decimal PricePerKwh { get; set; }
        public decimal UserSessionFee { get; set; }
        public decimal OwnerSessionFee { get; set; }
        public decimal OwnerCommissionPercent { get; set; }
        public decimal OwnerCommissionFixedPerKwh { get; set; }
        public double MaxSessionKwh { get; set; }
        public int StartUsageFeeAfterMinutes { get; set; }
        public int MaxUsageFeeMinutes { get; set; }
        public decimal ConnectorUsageFeePerMinute { get; set; }
        public bool UsageFeeAfterChargingEnds { get; set; }
        public int? OwnerId { get; set; }

        public virtual Owner Owner { get; set; }
        public virtual ICollection<Transaction> Transactions { get; set; }
    }
}
