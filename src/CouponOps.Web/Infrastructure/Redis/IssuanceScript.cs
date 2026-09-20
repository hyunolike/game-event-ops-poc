using StackExchange.Redis;

namespace CouponOps.Infrastructure.Redis;

/// <summary>선착순 쿠폰 발급 스크립트.</summary>
public sealed class IssuanceScript(IConnectionMultiplexer mux) : LuaScript(mux, "issue_coupon.lua");
