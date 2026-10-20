using System;

namespace AetherisLauncher.Models
{
    public class AuthAccount
    {
        public string Username { get; set; } = "Player";
        public string Uuid { get; set; } = Guid.NewGuid().ToString("N");
        public string Xuid { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
        public bool IsOffline { get; set; } = true;
        public string SkinUrl { get; set; } = "";
    }
}
