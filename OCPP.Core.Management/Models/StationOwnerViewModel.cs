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
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using OCPP.Core.Database;

namespace OCPP.Core.Management.Models
{
    public class StationOwnerViewModel
    {
        public StationOwnerViewModel()
        {
            Owners = new List<ChargeStationOwner>();
        }

        public List<ChargeStationOwner> Owners { get; set; }

        public string CurrentId { get; set; }

        public int? OwnerId { get; set; }

        [Required, StringLength(200)]
        public string Name { get; set; }

        [Required, EmailAddress, StringLength(200)]
        public string Email { get; set; }

        [Range(0, 100)]
        public decimal ProvisionPercentage { get; set; }

        public int? LastReportYear { get; set; }

        public int? LastReportMonth { get; set; }

        public DateTime? LastReportSentAt { get; set; }
    }
}
