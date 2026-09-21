using StackExchange.Redis;

namespace CouponOps.Infrastructure.Redis;

/// <summary>룰렛 추첨 스크립트.</summary>
public sealed class DrawScript(IConnectionMultiplexer mux) : LuaScript(mux, "draw_spin.lua");
