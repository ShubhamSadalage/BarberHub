using BarberHub.Web.Domain.Entities;
using BarberHub.Web.Infrastructure.Persistence;
using BarberHub.Web.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BarberHub.Web.Features.Chat;

public class ChatMessageDto
{
    public Guid Id { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string ReceiverId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public bool IsRead { get; set; }
}

public class ChatContactDto
{
    public string UserId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? ProfileImageUrl { get; set; }
    public string? LastMessage { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
}

[Authorize]
public class ChatHub : Hub
{
    private readonly ApplicationDbContext _context;
    private readonly Notifications.INotificationService _notifications;

    public ChatHub(ApplicationDbContext context, Notifications.INotificationService notifications)
    {
        _context = context;
        _notifications = notifications;
    }

    public async Task SendMessage(string receiverId, string content)
    {
        var senderId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(receiverId) || string.IsNullOrWhiteSpace(content))
            return;

        var message = new ChatMessage
        {
            SenderId = senderId,
            ReceiverId = receiverId,
            Content = content.Trim(),
            SentAt = DateTime.UtcNow
        };

        await _context.ChatMessages.AddAsync(message);
        await _context.SaveChangesAsync();

        var sender = await _context.Users.FindAsync(senderId);

        var dto = new ChatMessageDto
        {
            Id = message.Id,
            SenderId = senderId,
            SenderName = sender?.FullName ?? "",
            ReceiverId = receiverId,
            Content = message.Content,
            SentAt = message.SentAt
        };

        await Clients.User(receiverId).SendAsync("ReceiveMessage", dto);
        await Clients.Caller.SendAsync("MessageSent", dto);

        // In-app notification so the recipient sees a bell badge / toast
        // even if their chat window isn't open.
        var preview = message.Content.Length > 80 ? message.Content[..80] + "…" : message.Content;
        await _notifications.NotifyAsync(
            receiverId,
            Domain.Entities.NotificationType.NewMessage,
            $"New message from {sender?.FullName ?? "someone"}",
            preview,
            $"/Chat/Conversation?otherUserId={senderId}");
    }
}

[Authorize]
public class ChatController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ICurrentUserService _currentUser;

    public ChatController(ApplicationDbContext context, ICurrentUserService currentUser)
    {
        _context = context;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var userId = _currentUser.UserId!;

        var contactIds = await _context.ChatMessages
            .Where(m => m.SenderId == userId || m.ReceiverId == userId)
            .Select(m => m.SenderId == userId ? m.ReceiverId : m.SenderId)
            .Distinct()
            .ToListAsync();

        var contacts = new List<ChatContactDto>();
        foreach (var cid in contactIds)
        {
            var user = await _context.Users.FindAsync(cid);
            if (user is null) continue;

            var lastMsg = await _context.ChatMessages
                .Where(m => (m.SenderId == userId && m.ReceiverId == cid) || (m.SenderId == cid && m.ReceiverId == userId))
                .OrderByDescending(m => m.SentAt)
                .FirstOrDefaultAsync();

            var unread = await _context.ChatMessages
                .CountAsync(m => m.SenderId == cid && m.ReceiverId == userId && !m.IsRead);

            contacts.Add(new ChatContactDto
            {
                UserId = user.Id,
                FullName = user.ShopName ?? user.FullName,
                ProfileImageUrl = user.ProfileImageUrl,
                LastMessage = lastMsg?.Content,
                LastMessageAt = lastMsg?.SentAt,
                UnreadCount = unread
            });
        }

        return View(contacts.OrderByDescending(c => c.LastMessageAt).ToList());
    }

    [HttpGet]
    public async Task<IActionResult> Conversation(string otherUserId)
    {
        var userId = _currentUser.UserId!;
        var other = await _context.Users.FindAsync(otherUserId);
        if (other is null) return NotFound();

        var messages = await _context.ChatMessages
            .Where(m => (m.SenderId == userId && m.ReceiverId == otherUserId)
                     || (m.SenderId == otherUserId && m.ReceiverId == userId))
            .OrderBy(m => m.SentAt)
            .ToListAsync();

        // mark incoming messages as read
        foreach (var msg in messages.Where(m => m.ReceiverId == userId && !m.IsRead))
            msg.IsRead = true;
        await _context.SaveChangesAsync();

        ViewBag.OtherUser = other;
        ViewBag.CurrentUserId = userId;

        return View(messages.Select(m => new ChatMessageDto
        {
            Id = m.Id,
            SenderId = m.SenderId,
            ReceiverId = m.ReceiverId,
            Content = m.Content,
            SentAt = m.SentAt,
            IsRead = m.IsRead
        }).ToList());
    }
}
