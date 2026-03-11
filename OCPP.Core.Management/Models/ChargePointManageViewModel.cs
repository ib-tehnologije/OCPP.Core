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
using OCPP.Core.Database;

namespace OCPP.Core.Management.Models
{
    public class ChargePointManageViewModel
    {
        public ChargePoint ChargePoint { get; set; }

        public List<ConnectorStatus> ConnectorStatuses { get; set; }

        public List<ChargeTag> ChargeTags { get; set; }

        public Dictionary<string, ChargePointStatus> OnlineConnectorStatuses { get; set; }

        public List<AlfenConfigOption> ConfigOptions { get; set; }

        public List<ChargePointManageLiveConnectorViewModel> LiveConnectors { get; set; } = new List<ChargePointManageLiveConnectorViewModel>();
    }

    public class ChargePointManageLiveConnectorViewModel
    {
        public int ConnectorId { get; set; }
        public string ConnectorName { get; set; }
        public string LiveStatus { get; set; }
        public string LiveOcppStatus { get; set; }
        public double? ChargeRateKw { get; set; }
        public double? CurrentImportA { get; set; }
        public double? MeterKwh { get; set; }
        public double? SessionEnergyKwh { get; set; }
        public double? SoC { get; set; }
        public int? ActiveTransactionId { get; set; }
        public string ActiveTagId { get; set; }
        public System.DateTime? StartedAtUtc { get; set; }
        public Guid? ActiveReservationId { get; set; }
        public string ActiveReservationStatus { get; set; }
        public bool CanCancelReservation { get; set; }
    }
}
