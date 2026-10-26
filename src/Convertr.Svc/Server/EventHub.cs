// Copyright (C) 2026 JeepNWilly
// Convertr_svc is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.Text.Json;
using System.Threading.Channels;

namespace Convertr.Svc.Server;

/// <summary>Fan-out of server events to the connected browsers (Server-Sent Events). Slow clients drop old events instead of blocking.</summary>
public sealed class EventHub
{
    public sealed record Message(string Type, string Json);

    private readonly object _lock = new();
    private readonly List<Channel<Message>> _subs = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ChannelReader<Message> Subscribe(out Action unsubscribe)
    {
        var ch = Channel.CreateBounded<Message>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_lock) _subs.Add(ch);
        unsubscribe = () => { lock (_lock) _subs.Remove(ch); ch.Writer.TryComplete(); };
        return ch.Reader;
    }

    public int Subscribers { get { lock (_lock) return _subs.Count; } }

    public void Publish(string type, object payload)
    {
        var msg = new Message(type, JsonSerializer.Serialize(payload, Json));
        lock (_lock) foreach (var s in _subs) s.Writer.TryWrite(msg);
    }
}
