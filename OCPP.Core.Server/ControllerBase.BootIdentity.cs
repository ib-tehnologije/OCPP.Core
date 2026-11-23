using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server
{
    public partial class ControllerBase
    {
        // ChargePoint columns filled from BootNotification.
        private static readonly string[] BootIdentityProperties =
        {
            nameof(ChargePoint.ChargePointVendor),
            nameof(ChargePoint.ChargePointModel),
            nameof(ChargePoint.ChargePointSerialNumber),
            nameof(ChargePoint.ChargeBoxSerialNumber),
            nameof(ChargePoint.FirmwareVersion),
            nameof(ChargePoint.Iccid),
            nameof(ChargePoint.Imsi),
            nameof(ChargePoint.MeterType),
            nameof(ChargePoint.MeterSerialNumber),
        };

        // Serial numbers identify the device. When one can be compared, it alone decides whether the boot
        // comes from the stored charger.
        private static readonly string[] SerialNumberProperties =
        {
            nameof(ChargePoint.ChargePointSerialNumber),
            nameof(ChargePoint.ChargeBoxSerialNumber),
        };

        // Compared only when no serial number can be, ignoring case: the same charger can report its vendor
        // or model written differently, for example after a firmware update or in another OCPP version.
        private static readonly string[] VendorModelProperties =
        {
            nameof(ChargePoint.ChargePointVendor),
            nameof(ChargePoint.ChargePointModel),
        };

        /// <summary>
        /// Stores the identity a charger reports in BootNotification on its ChargePoint row.
        /// Keys are ChargePoint property names. Values are trimmed and cut to the column length, because
        /// schema validation is optional and does not reject messages.
        /// For the same device, only values the charger sent overwrite stored ones, so a boot without
        /// (for example) a modem ICCID keeps the last known value.
        /// The boot comes from another device when a serial number (charge point or charge box) differs from
        /// the stored one, or, when no serial number is present on both sides, when the vendor or model differs.
        /// Then every identity column is set to what this boot sent, and the columns it did not send are cleared.
        /// Values missing on either side are never compared, so a replacement of the same vendor and model that
        /// sends no serial number cannot be told apart from the stored charger.
        /// A failure is logged and never changes the BootNotification response.
        /// </summary>
        protected void UpdateChargePointBootIdentity(IReadOnlyDictionary<string, string> reported)
        {
            if (ChargePointStatus == null || reported == null)
            {
                return;
            }

            ChargePoint chargePoint = null;
            try
            {
                chargePoint = DbContext.ChargePoints.Find(ChargePointStatus.Id);
                if (chargePoint == null)
                {
                    Logger.LogWarning("BootNotification => Charge point '{0}' not found; identity not stored", ChargePointStatus.Id);
                    return;
                }

                var entry = DbContext.Entry(chargePoint);
                var values = new Dictionary<string, string>();
                foreach (string name in BootIdentityProperties)
                {
                    reported.TryGetValue(name, out string raw);
                    values[name] = NormalizeIdentityValue(entry.Property(name), raw);
                }

                string anotherDevice = DescribeAnotherDevice(entry, values);

                bool changed = false;
                var cleared = new List<string>();
                foreach (string name in BootIdentityProperties)
                {
                    string value = values[name];
                    if (value == null && anotherDevice == null)
                    {
                        // Same device: keep the last known value.
                        continue;
                    }

                    var property = entry.Property(name);
                    if (!string.Equals(property.CurrentValue as string, value, StringComparison.Ordinal))
                    {
                        if (value == null)
                        {
                            cleared.Add(name);
                        }
                        property.CurrentValue = value;
                        changed = true;
                    }
                }

                if (changed)
                {
                    DbContext.SaveChanges();
                    if (anotherDevice == null)
                    {
                        Logger.LogInformation("BootNotification => Stored identity of charge point '{0}'", ChargePointStatus.Id);
                    }
                    else if (cleared.Count == 0)
                    {
                        Logger.LogWarning("BootNotification => Charge point '{0}' reports another device ({1}); stored the identity this boot sent",
                            ChargePointStatus.Id, anotherDevice);
                    }
                    else
                    {
                        Logger.LogWarning("BootNotification => Charge point '{0}' reports another device ({1}); stored the identity this boot sent and cleared {2}",
                            ChargePointStatus.Id, anotherDevice, string.Join(", ", cleared));
                    }
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "BootNotification => Could not store identity of charge point '{0}': {1}", ChargePointStatus.Id, exp.Message);
                if (chargePoint != null)
                {
                    // Undo the pending changes so the message log write that follows does not retry them.
                    var entry = DbContext.Entry(chargePoint);
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                }
            }
        }

        /// <summary>
        /// The device fields that show this boot comes from another charger, as "Name 'stored' -> 'reported'",
        /// or null for the same charger. Only fields with a stored and a reported value are compared.
        /// </summary>
        private static string DescribeAnotherDevice(EntityEntry<ChargePoint> entry, IReadOnlyDictionary<string, string> values)
        {
            var serialNumbers = ComparableValues(entry, values, SerialNumberProperties);
            if (serialNumbers.Count > 0)
            {
                return DescribeDifferences(serialNumbers, StringComparison.Ordinal);
            }
            return DescribeDifferences(ComparableValues(entry, values, VendorModelProperties), StringComparison.OrdinalIgnoreCase);
        }

        private static List<(string Name, string Stored, string Reported)> ComparableValues(
            EntityEntry<ChargePoint> entry, IReadOnlyDictionary<string, string> values, string[] names)
        {
            return names
                .Select(name => (Name: name, Stored: entry.Property(name).CurrentValue as string, Reported: values[name]))
                .Where(v => !string.IsNullOrEmpty(v.Stored) && v.Reported != null)
                .ToList();
        }

        private static string DescribeDifferences(List<(string Name, string Stored, string Reported)> compared, StringComparison comparison)
        {
            var differences = compared
                .Where(v => !string.Equals(v.Stored, v.Reported, comparison))
                .Select(v => $"{v.Name} '{v.Stored}' -> '{v.Reported}'")
                .ToList();
            return differences.Count > 0 ? string.Join(", ", differences) : null;
        }

        /// <summary>Trimmed value cut to the column length, or null when nothing was sent.</summary>
        private string NormalizeIdentityValue(PropertyEntry property, string raw)
        {
            string value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            int? maxLength = property.Metadata.GetMaxLength();
            if (maxLength.HasValue && value.Length > maxLength.Value)
            {
                int length = maxLength.Value;
                if (char.IsHighSurrogate(value[length - 1]))
                {
                    // Do not split a surrogate pair (for example an emoji) at the cut.
                    length--;
                }
                Logger.LogWarning("BootNotification => {0} is {1} characters long; storing the first {2}", property.Metadata.Name, value.Length, length);
                value = value.Substring(0, length);
            }
            return value;
        }
    }
}
