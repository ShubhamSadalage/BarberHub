using BarberHub.Web.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BarberHub.Web.Infrastructure.Persistence;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<Service> Services => Set<Service>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<LeaveDay> LeaveDays => Set<LeaveDay>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ApplicationUser
        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(e => e.FullName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ShopName).HasMaxLength(200);
            entity.Property(e => e.ShopDescription).HasMaxLength(1000);
            entity.Property(e => e.Address).HasMaxLength(500);
            entity.Property(e => e.City).HasMaxLength(100);
            entity.Property(e => e.ApprovalRejectionReason).HasMaxLength(500);
            entity.HasIndex(e => e.PhoneNumber).IsUnique(false);
            entity.HasIndex(e => e.City);
            entity.HasIndex(e => e.ApprovalStatus);
        });

        // Service
        builder.Entity<Service>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Category).HasMaxLength(50).HasDefaultValue("Hair");
            entity.Property(e => e.Price).HasColumnType("decimal(10,2)");

            entity.HasOne(e => e.Barber)
                  .WithMany(u => u.Services)
                  .HasForeignKey(e => e.BarberId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.BarberId);
            entity.HasIndex(e => new { e.BarberId, e.IsActive });
        });

        // Product
        builder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.Price).HasColumnType("decimal(10,2)");
            entity.Property(e => e.DiscountPercentage).HasColumnType("decimal(5,2)");
            entity.Ignore(e => e.EffectivePrice);
            entity.Ignore(e => e.IsMarketplace);

            // BarberId is now nullable: null = marketplace product (SuperAdmin owned)
            entity.HasOne(e => e.Barber)
                  .WithMany(u => u.Products)
                  .HasForeignKey(e => e.BarberId)
                  .OnDelete(DeleteBehavior.Cascade)
                  .IsRequired(false);

            entity.HasIndex(e => e.BarberId);
            entity.HasIndex(e => e.Category);
        });

        // Notification
        builder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.LinkUrl).HasMaxLength(500);

            entity.HasOne(e => e.Recipient)
                  .WithMany()
                  .HasForeignKey(e => e.RecipientUserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.RecipientUserId, e.IsRead });
            entity.HasIndex(e => e.CreatedAt);
        });

        // Booking
        builder.Entity<Booking>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.RejectionReason).HasMaxLength(500);

            entity.HasOne(e => e.User)
                  .WithMany(u => u.UserBookings)
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Barber)
                  .WithMany(u => u.BarberBookings)
                  .HasForeignKey(e => e.BarberId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Service)
                  .WithMany(s => s.Bookings)
                  .HasForeignKey(e => e.ServiceId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.BarberId, e.AppointmentDate });
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Status);
        });

        // LeaveDay
        builder.Entity<LeaveDay>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).HasMaxLength(300);

            entity.HasOne(e => e.Barber)
                  .WithMany(u => u.LeaveDays)
                  .HasForeignKey(e => e.BarberId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.BarberId, e.LeaveDate }).IsUnique();
        });

        // Order
        builder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OrderNumber).HasMaxLength(50).IsRequired();
            entity.HasIndex(e => e.OrderNumber).IsUnique();

            entity.Property(e => e.SubTotal).HasColumnType("decimal(10,2)");
            entity.Property(e => e.Tax).HasColumnType("decimal(10,2)");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(10,2)");
            entity.Property(e => e.ShippingAddress).HasMaxLength(1000);
            entity.Property(e => e.PaymentIntentId).HasMaxLength(200);
            entity.Property(e => e.PaymentStatus).HasMaxLength(50);

            entity.HasOne(e => e.User)
                  .WithMany(u => u.Orders)
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.UserId);
        });

        // OrderItem
        builder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProductName).HasMaxLength(200);
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(10,2)");
            entity.Ignore(e => e.LineTotal);

            entity.HasOne(e => e.Order)
                  .WithMany(o => o.Items)
                  .HasForeignKey(e => e.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Product)
                  .WithMany(p => p.OrderItems)
                  .HasForeignKey(e => e.ProductId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ChatMessage
        builder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).HasMaxLength(2000).IsRequired();

            entity.HasOne(e => e.Sender)
                  .WithMany()
                  .HasForeignKey(e => e.SenderId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Receiver)
                  .WithMany()
                  .HasForeignKey(e => e.ReceiverId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.SenderId, e.ReceiverId });
            entity.HasIndex(e => e.SentAt);
        });

        // CartItem
        builder.Entity<CartItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Product)
                  .WithMany()
                  .HasForeignKey(e => e.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.ProductId }).IsUnique();
        });

        // OtpCode
        builder.Entity<OtpCode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Recipient).HasMaxLength(160).IsRequired();
            entity.Property(e => e.CodeHash).HasMaxLength(128).IsRequired();
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.HasIndex(e => new { e.Recipient, e.Purpose, e.ConsumedAt });
            entity.HasIndex(e => e.ExpiresAt);
        });

        // AuditLog
        builder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActorUserId).HasMaxLength(450).IsRequired();
            entity.Property(e => e.ActorEmail).HasMaxLength(160).IsRequired();
            entity.Property(e => e.ActorRole).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(128).IsRequired();
            entity.Property(e => e.TargetType).HasMaxLength(128);
            entity.Property(e => e.TargetId).HasMaxLength(450);
            entity.Property(e => e.Details).HasColumnType("text");
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.ActorUserId, e.CreatedAt });
            entity.HasIndex(e => new { e.TargetType, e.TargetId });
        });

        // PushSubscription
        builder.Entity<PushSubscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Endpoint).HasMaxLength(500).IsRequired();
            entity.Property(e => e.P256dh).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Auth).HasMaxLength(100).IsRequired();
            entity.Property(e => e.UserAgent).HasMaxLength(500);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.Endpoint).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.IsActive });
        });

        // Indexes for nearby-barbers query
        builder.Entity<ApplicationUser>(entity =>
        {
            entity.HasIndex(e => new { e.Latitude, e.Longitude });
            entity.HasIndex(e => e.IsDeleted);
            entity.HasIndex(e => e.GoogleSubjectId);
        });

        // Soft-delete indexes
        builder.Entity<Product>().HasIndex(e => e.IsDeleted);

        // Booking queue index — used by the queue background service
        builder.Entity<Booking>().HasIndex(e => new { e.BarberId, e.AppointmentDate, e.Status });
    }
}
