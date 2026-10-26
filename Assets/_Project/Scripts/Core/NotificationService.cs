using System;
using System.Collections.Generic;

namespace Noxy.Core
{
    public enum NotificationSeverity
    {
        Info,
        Success,
        Warning,
        Error
    }

    public sealed class NotificationMessage
    {
        public string Title;
        public string Body;
        public NotificationSeverity Severity;
        public DateTime TimeUtc;
        public bool Read;
    }

    public sealed class NotificationService
    {
        private readonly List<NotificationMessage> messages = new List<NotificationMessage>();

        public IReadOnlyList<NotificationMessage> Messages => messages;

        public int UnreadCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < messages.Count; i++) if (!messages[i].Read) n++;
                return n;
            }
        }

        public event Action Changed;
        public event Action<NotificationMessage> ToastRequested;

        public NotificationMessage Post(string title, string body, NotificationSeverity severity = NotificationSeverity.Info, bool toast = true)
        {
            var message = new NotificationMessage
            {
                Title = title, Body = body, Severity = severity, TimeUtc = DateTime.UtcNow
            };
            messages.Insert(0, message);
            if (messages.Count > 50) messages.RemoveAt(messages.Count - 1);
            Changed?.Invoke();
            if (toast) ToastRequested?.Invoke(message);
            return message;
        }

        public void MarkAllRead()
        {
            bool any = false;
            foreach (var m in messages)
            {
                if (!m.Read) { m.Read = true; any = true; }
            }
            if (any) Changed?.Invoke();
        }

        public void Clear()
        {
            if (messages.Count == 0) return;
            messages.Clear();
            Changed?.Invoke();
        }
    }
}
