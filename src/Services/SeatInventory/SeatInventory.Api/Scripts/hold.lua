-- Atomically hold ALL requested seats or none (design §5, guard 1).
-- All keys share the {showId} hash tag, so the script runs on one shard.
-- KEYS: 1 holds:{s} (zset seat->expiryMs)  2 booked:{s} (set)  3 owner:{s} (hash seat->holdId)
--       4 hold:{s}:{h} (hash)  5 userhold:{s}:{u} (string)  6 seq:{s} (counter)  7 seats:{s} (set of valid seats)
-- ARGV: 1 nowMs  2 expiryMs  3 graceMs  4 userId  5 holdId  6.. seatIds
-- Returns {1, seq} on success, {0, code, seatId?} on rejection.
local now, expiry, grace = tonumber(ARGV[1]), tonumber(ARGV[2]), tonumber(ARGV[3])

if redis.call('EXISTS', KEYS[7]) == 0 then return {0, 'SHOW_NOT_ON_SALE'} end
if redis.call('EXISTS', KEYS[5]) == 1 then return {0, 'USER_HAS_ACTIVE_HOLD'} end

redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)          -- lazy expiry of old holds

for i = 6, #ARGV do
  local seat = ARGV[i]
  if redis.call('SISMEMBER', KEYS[7], seat) == 0 then return {0, 'UNKNOWN_SEAT', seat} end
  if redis.call('SISMEMBER', KEYS[2], seat) == 1 then return {0, 'SEAT_BOOKED', seat} end
  if redis.call('ZSCORE', KEYS[1], seat) then return {0, 'SEAT_HELD', seat} end
end

local seats = {}
for i = 6, #ARGV do
  redis.call('ZADD', KEYS[1], expiry, ARGV[i])
  redis.call('HSET', KEYS[3], ARGV[i], ARGV[5])
  seats[#seats + 1] = ARGV[i]
end

redis.call('HSET', KEYS[4], 'user', ARGV[4], 'seats', table.concat(seats, ','), 'expires', expiry, 'state', 'ACTIVE')
redis.call('PEXPIREAT', KEYS[4], expiry + grace)              -- kept past expiry so the expiry check can read it
redis.call('SET', KEYS[5], ARGV[5], 'PXAT', expiry)
return {1, redis.call('INCR', KEYS[6])}
