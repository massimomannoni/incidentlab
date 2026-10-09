using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;

namespace IncidentLab.Gateway;

// One gateway process owns a bounded store. Restart/credential rotation revokes every session.
// Tickets also contain an absolute expiry, checked independently by the cookie middleware.
public sealed class SessionTickets(TimeProvider clock) : ITicketStore, IDisposable
{
    readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1024 });
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Put(key, ticket);
        if (!cache.TryGetValue(key, out _)) throw new InvalidOperationException("Session capacity reached");
        return Task.FromResult(key);
    }
    void Put(string key, AuthenticationTicket ticket)
    {
        var remaining = ticket.Properties.ExpiresUtc!.Value - clock.GetUtcNow();
        if (remaining > TimeSpan.Zero) cache.Set(key, ticket, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = remaining });
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket) { Put(key, ticket); return Task.CompletedTask; }
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var ticket = cache.Get<AuthenticationTicket>(key);
        if (ticket?.Properties.ExpiresUtc <= clock.GetUtcNow()) { cache.Remove(key); ticket = null; }
        return Task.FromResult(ticket);
    }
    public Task RemoveAsync(string key) { cache.Remove(key); return Task.CompletedTask; }
    public void Dispose() => cache.Dispose();
}
