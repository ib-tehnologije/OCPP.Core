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

using OCPP.Core.Database;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Management.Models
{
    public class ChargePointViewModel
    {
        public List<ChargePoint> ChargePoints { get; set; }

        public string CurrentId { get; set; }


        [Required, StringLength(100)]
        public string ChargePointId { get; set; }

        [Required, StringLength(100)]
        public string Name { get; set; }

        [StringLength(100)]
        public string Comment { get; set; }

        [StringLength(500)]
        public string Description { get; set; }

        [StringLength(50)]
        public string Username { get; set; }

        [StringLength(50)]
        public string Password { get; set; }

        [StringLength(100)]
        public string ClientCertThumb { get; set; }

        public bool FreeChargingEnabled { get; set; }

        [Range(0, 100000)]
        public double? MaxSessionKwh { get; set; }

        [Range(0, 10000)]
        public decimal? PricePerKwh { get; set; }

        [Range(0, 10000)]
        public decimal? UserSessionFee { get; set; }

        [Range(0, 10000)]
        public decimal? OwnerSessionFee { get; set; }

        [Range(0, 100)]
        public decimal? OwnerCommissionPercent { get; set; }

        [Range(0, 10000)]
        public decimal? OwnerCommissionFixedPerKwh { get; set; }

        [Range(0, 100000)]
        public int? StartUsageFeeAfterMinutes { get; set; }

        [Range(0, 100000)]
        public int? MaxUsageFeeMinutes { get; set; }

        [Range(0, 10000)]
        public decimal? ConnectorUsageFeePerMinute { get; set; }

        public bool UsageFeeAfterChargingEnds { get; set; }

        [Range(-90, 90)]
        public double? Latitude { get; set; }

        [Range(-180, 180)]
        public double? Longitude { get; set; }

        [StringLength(500)]
        public string LocationDescription { get; set; }

        public int? OwnerId { get; set; }

        public List<Owner> Owners { get; set; } = new List<Owner>();
    }
}
