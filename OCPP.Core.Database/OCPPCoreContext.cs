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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;

#nullable disable

namespace OCPP.Core.Database
{
    public partial class OCPPCoreContext : DbContext
    {
        private static readonly EntityState[] ReservationSyncStates = new[] { EntityState.Added, EntityState.Modified };

        public OCPPCoreContext(DbContextOptions<OCPPCoreContext> options)
            : base(options)
        {
        }

        public virtual DbSet<Owner> Owners { get; set; }
        public virtual DbSet<ChargePoint> ChargePoints { get; set; }
        public virtual DbSet<ChargeTag> ChargeTags { get; set; }
        public virtual DbSet<ChargeTagPrivilege> ChargeTagPrivileges { get; set; }
        public virtual DbSet<ConnectorStatus> ConnectorStatuses { get; set; }
        public virtual DbSet<InvoiceSubmissionLog> InvoiceSubmissionLogs { get; set; }
        public virtual DbSet<MessageLog> MessageLogs { get; set; }
        public virtual DbSet<PublicPortalSettings> PublicPortalSettings { get; set; }
        public virtual DbSet<Transaction> Transactions { get; set; }
        public virtual DbSet<ChargePaymentReservation> ChargePaymentReservations { get; set; }
        public virtual DbSet<StripeWebhookEvent> StripeWebhookEvents { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ChargePoint>(entity =>
            {
                entity.ToTable("ChargePoint");

                entity.HasIndex(e => e.ChargePointId, "ChargePoint_Identifier")
                    .IsUnique();

                entity.Property(e => e.ChargePointId).HasMaxLength(100);

                entity.Property(e => e.Comment).HasMaxLength(200);

                entity.Property(e => e.Description).HasMaxLength(500);

                entity.Property(e => e.Name).HasMaxLength(100);

                entity.Property(e => e.Username).HasMaxLength(50);

                entity.Property(e => e.Password).HasMaxLength(50);

                entity.Property(e => e.ClientCertThumb).HasMaxLength(100);

                entity.Property(e => e.PricePerKwh).HasColumnType("decimal(18,4)");
                entity.Property(e => e.UserSessionFee).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerSessionFee).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerCommissionPercent).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerCommissionFixedPerKwh).HasColumnType("decimal(18,4)");

                entity.Property(e => e.ConnectorUsageFeePerMinute).HasColumnType("decimal(18,4)");

                entity.Property(e => e.UsageFeeAfterChargingEnds).HasColumnType("bit");
                entity.Property(e => e.Latitude).HasColumnType("float");
                entity.Property(e => e.Longitude).HasColumnType("float");
                entity.Property(e => e.LocationDescription).HasMaxLength(500);

                entity.Property(e => e.UsageFeeAfterChargingEnds).HasColumnType("bit");

                entity.HasOne(d => d.Owner)
                    .WithMany(p => p.ChargePoints)
                    .HasForeignKey(d => d.OwnerId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<Owner>(entity =>
            {
                entity.ToTable("Owner");

                entity.HasKey(e => e.OwnerId);

                entity.Property(e => e.OwnerId)
                    .ValueGeneratedOnAdd();

                entity.Property(e => e.Name)
                    .HasMaxLength(200)
                    .IsRequired();

                entity.Property(e => e.Email)
                    .HasMaxLength(200);
            });

            modelBuilder.Entity<ChargeTag>(entity =>
            {
                entity.HasKey(e => e.TagId)
                    .HasName("PK_ChargeKeys");

                entity.Property(e => e.TagId).HasMaxLength(50);

                entity.Property(e => e.ParentTagId).HasMaxLength(50);

                entity.Property(e => e.TagName).HasMaxLength(200);
            });

            modelBuilder.Entity<ChargeTagPrivilege>(entity =>
            {
                entity.ToTable("ChargeTagPrivilege");

                entity.HasKey(e => e.Id);

                entity.Property(e => e.TagId)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.Property(e => e.ChargePointId)
                    .HasMaxLength(100);

                entity.Property(e => e.Note)
                    .HasMaxLength(200);

                entity.Property(e => e.CreatedAtUtc)
                    .HasDefaultValueSql("getutcdate()");

                entity.HasIndex(e => new { e.TagId, e.ChargePointId })
                    .HasDatabaseName("IX_ChargeTagPrivilege_Tag_Point");

                entity.HasOne(d => d.Tag)
                    .WithMany()
                    .HasForeignKey(d => d.TagId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(d => d.ChargePoint)
                    .WithMany()
                    .HasForeignKey(d => d.ChargePointId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<ConnectorStatus>(entity =>
            {
                entity.HasKey(e => new { e.ChargePointId, e.ConnectorId });

                entity.ToTable("ConnectorStatus");

                entity.Property(e => e.ChargePointId).HasMaxLength(100);

                entity.Property(e => e.ConnectorName).HasMaxLength(100);

                entity.Property(e => e.LastStatus).HasMaxLength(100);
            });

            modelBuilder.Entity<StripeWebhookEvent>(entity =>
            {
                entity.HasKey(e => e.EventId);

                entity.Property(e => e.EventId)
                    .HasMaxLength(200)
                    .IsRequired();

                entity.Property(e => e.Type)
                    .HasMaxLength(200);

                entity.Property(e => e.ProcessedAtUtc)
                    .IsRequired();

                entity.HasIndex(e => e.ProcessedAtUtc);
            });

            modelBuilder.Entity<PublicPortalSettings>(entity =>
            {
                entity.ToTable("PublicPortalSettings");

                entity.HasKey(e => e.PublicPortalSettingsId);

                entity.Property(e => e.BrandName).HasMaxLength(200);
                entity.Property(e => e.Tagline).HasMaxLength(300);
                entity.Property(e => e.SupportEmail).HasMaxLength(200);
                entity.Property(e => e.SupportPhone).HasMaxLength(100);
                entity.Property(e => e.HelpUrl).HasMaxLength(500);
                entity.Property(e => e.FooterCompanyLine).HasMaxLength(300);
                entity.Property(e => e.FooterAddressLine).HasMaxLength(300);
                entity.Property(e => e.FooterLegalLine).HasMaxLength(300);
                entity.Property(e => e.CanonicalBaseUrl).HasMaxLength(500);
                entity.Property(e => e.SeoTitle).HasMaxLength(300);
                entity.Property(e => e.SeoDescription).HasMaxLength(500);
                entity.Property(e => e.HeaderLogoUrl).HasMaxLength(500);
                entity.Property(e => e.FooterLogoUrl).HasMaxLength(500);
                entity.Property(e => e.IdleFeeExcludedWindow).HasMaxLength(32);
                entity.Property(e => e.CreatedAtUtc).HasDefaultValueSql("getutcdate()");
                entity.Property(e => e.UpdatedAtUtc).HasDefaultValueSql("getutcdate()");
            });

            modelBuilder.Entity<InvoiceSubmissionLog>(entity =>
            {
                entity.HasKey(e => e.InvoiceSubmissionLogId);

                entity.ToTable("InvoiceSubmissionLog");

                entity.Property(e => e.Provider)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.Property(e => e.Mode)
                    .HasMaxLength(50);

                entity.Property(e => e.Status)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.Property(e => e.InvoiceKind)
                    .HasMaxLength(50);

                entity.Property(e => e.ProviderOperation)
                    .HasMaxLength(100);

                entity.Property(e => e.ApiTransactionId)
                    .HasMaxLength(100);

                entity.Property(e => e.StripeCheckoutSessionId)
                    .HasMaxLength(200);

                entity.Property(e => e.StripePaymentIntentId)
                    .HasMaxLength(200);

                entity.Property(e => e.ExternalDocumentId)
                    .HasMaxLength(200);

                entity.Property(e => e.ExternalInvoiceNumber)
                    .HasMaxLength(200);

                entity.Property(e => e.ExternalPublicUrl)
                    .HasMaxLength(1000);

                entity.Property(e => e.ExternalPdfUrl)
                    .HasMaxLength(1000);

                entity.Property(e => e.ProviderResponseStatus)
                    .HasMaxLength(100);

                entity.Property(e => e.Error)
                    .HasMaxLength(4000);

                entity.HasIndex(e => new { e.ReservationId, e.CreatedAtUtc })
                    .HasDatabaseName("IX_InvoiceSubmissionLog_Reservation_Created");

                entity.HasIndex(e => e.TransactionId)
                    .HasDatabaseName("IX_InvoiceSubmissionLog_Transaction");

                entity.HasIndex(e => e.CreatedAtUtc)
                    .HasDatabaseName("IX_InvoiceSubmissionLog_Created");
            });

            modelBuilder.Entity<MessageLog>(entity =>
            {
                entity.HasKey(e => e.LogId);

                entity.ToTable("MessageLog");

                entity.HasIndex(e => e.LogTime, "IX_MessageLog_ChargePointId");

                entity.Property(e => e.ChargePointId)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(e => e.ErrorCode).HasMaxLength(100);

                entity.Property(e => e.Message)
                    .IsRequired()
                    .HasMaxLength(100);
            });

            modelBuilder.Entity<Transaction>(entity =>
            {
                entity.Property(e => e.Uid).HasMaxLength(50);

                entity.Property(e => e.ChargePointId)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(e => e.StartTagId).HasMaxLength(50);

                entity.Property(e => e.StartResult).HasMaxLength(100);

                entity.Property(e => e.StopTagId).HasMaxLength(50);

                entity.Property(e => e.StopReason).HasMaxLength(100);

                entity.Property(e => e.Currency).HasMaxLength(10);
                entity.Property(e => e.FreeReason).HasMaxLength(200);
                entity.Property(e => e.EnergyCost).HasColumnType("decimal(18,4)");
                entity.Property(e => e.UserSessionFeeAmount).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerSessionFeeAmount).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerCommissionPercent).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerCommissionFixedPerKwh).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OperatorCommissionAmount).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OperatorRevenueTotal).HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerPayoutTotal).HasColumnType("decimal(18,4)");
                entity.Property(e => e.UsageFeeAmount).HasColumnType("decimal(18,4)");
                entity.Property(e => e.IdleUsageFeeAmount).HasColumnType("decimal(18,4)");

                entity.HasOne(d => d.ChargePoint)
                    .WithMany(p => p.Transactions)
                    .HasForeignKey(d => d.ChargePointId)
                    .OnDelete(DeleteBehavior.ClientSetNull)
                    .HasConstraintName("FK_Transactions_ChargePoint");

                entity.HasIndex(e => new { e.ChargePointId, e.ConnectorId });
            });

            modelBuilder.Entity<ChargePaymentReservation>(entity =>
            {
                entity.HasKey(e => e.ReservationId);

                entity.ToTable("ChargePaymentReservation");

                entity.Property(e => e.ChargePointId)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(e => e.ChargeTagId)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.Property(e => e.Currency)
                    .IsRequired()
                    .HasMaxLength(10);

                entity.Property(e => e.Status)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.Property(e => e.StripeCheckoutSessionId)
                    .HasMaxLength(200);

                entity.Property(e => e.StripePaymentIntentId)
                    .HasMaxLength(200);

                entity.Property(e => e.PricePerKwh)
                    .HasColumnType("decimal(18,4)");
                entity.Property(e => e.UserSessionFee)
                    .HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerSessionFee)
                    .HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerCommissionPercent)
                    .HasColumnType("decimal(18,4)");
                entity.Property(e => e.OwnerCommissionFixedPerKwh)
                    .HasColumnType("decimal(18,4)");

                entity.Property(e => e.UsageFeePerMinute)
                    .HasColumnType("decimal(18,4)");
                entity.Property(e => e.UsageFeeAnchorMinutes);

                entity.Property(e => e.LastError)
                    .HasMaxLength(500);

                entity.Property(e => e.FailureCode)
                    .HasMaxLength(100);

                entity.Property(e => e.FailureMessage)
                    .HasMaxLength(500);

                entity.Property(e => e.OcppIdTag)
                    .HasMaxLength(20);

                entity.Property(e => e.RemoteStartResult)
                    .HasMaxLength(50);

                entity.Property(e => e.StartTransactionId);
                entity.Property(e => e.AwaitingPlug);
                entity.Property(e => e.StartDeadlineAtUtc);
                entity.Property(e => e.RemoteStartSentAtUtc);
                entity.Property(e => e.RemoteStartAcceptedAtUtc);
                entity.Property(e => e.StartTransactionAtUtc);
                entity.Property(e => e.StopTransactionAtUtc);
                entity.Property(e => e.DisconnectedAtUtc);
                entity.Property(e => e.LastOcppEventAtUtc);

                entity.HasIndex(e => e.StripeCheckoutSessionId)
                    .HasDatabaseName("IX_PaymentReservations_StripeSession");

                entity.HasIndex(e => e.StripePaymentIntentId)
                    .HasDatabaseName("IX_PaymentReservations_PaymentIntent");

                entity.HasIndex(e => new { e.ChargePointId, e.ConnectorId, e.OcppIdTag })
                    .HasDatabaseName("IX_PaymentReservations_CpConnTag");

                // Prevent more than one active (non-completed) reservation per connector without relying on filtered indexes
                var activeKey = entity.Property<string>("ActiveConnectorKey")
                    .HasMaxLength(64)
                    .HasColumnType("nvarchar(64)");

                if (ChargePaymentReservationActiveKey.RequiresManualSync(Database.ProviderName))
                {
                    // InMemory/SQLite: keep the column writable and synchronize it in SaveChanges.
                    activeKey.HasDefaultValue(ChargePaymentReservationActiveKey.ActiveValue);
                }
                else
                {
                    activeKey
                        .IsRequired()
                        .HasComputedColumnSql("CASE WHEN [Status] NOT IN ('Completed','Cancelled','Failed','StartRejected','StartTimeout','Abandoned') THEN 'ACTIVE' ELSE CONVERT(nvarchar(36), [ReservationId]) END");
                }

                entity.HasIndex("ChargePointId", "ConnectorId", "ActiveConnectorKey")
                    .IsUnique()
                    .HasDatabaseName("UX_PaymentReservations_ActiveConnector");
            });

            OnModelCreatingPartial(modelBuilder);
        }

        public override int SaveChanges()
        {
            SynchronizeReservationActiveConnectorKeys();
            return base.SaveChanges();
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            SynchronizeReservationActiveConnectorKeys();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SynchronizeReservationActiveConnectorKeys();
            return base.SaveChangesAsync(cancellationToken);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            SynchronizeReservationActiveConnectorKeys();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private void SynchronizeReservationActiveConnectorKeys()
        {
            if (!ChargePaymentReservationActiveKey.RequiresManualSync(Database.ProviderName))
            {
                return;
            }

            ChangeTracker.DetectChanges();

            foreach (var entry in ChangeTracker.Entries<ChargePaymentReservation>().Where(e => ReservationSyncStates.Contains(e.State)))
            {
                var activeKey = entry.Property<string>("ActiveConnectorKey");
                var expected = ChargePaymentReservationActiveKey.Compute(entry.Entity.ReservationId, entry.Entity.Status);

                if (!string.Equals(activeKey.CurrentValue, expected, StringComparison.Ordinal))
                {
                    activeKey.CurrentValue = expected;
                }
            }
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
