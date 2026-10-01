-- Extend an active, unexpired hold (payment started: 5 min -> 10 min).
-- KEYS: 1 holds:{s}  2 owner:{s}  3 hold:{s}:{h}  4 userhold:{s}:{u}
-- ARGV: 1 holdId  2 newExpiryMs  3 graceMs  4 nowMs
-- Returns {1} | {0, code}
if redis.call('EXISTS', KEYS[3]) == 0 then return {0, 'NOT_FOUND'} end
if redis.call('HGET', KEYS[3], 'state') ~= 'ACTIVE' then return {0, 'NOT_ACTIVE'} end

local now, newExpiry = tonumber(ARGV[4]), tonumber(ARGV[2])
local expires = tonumber(redis.call('HGET', KEYS[3], 'expires'))
if expires <= now then return {0, 'EXPIRED'} end
if newExpiry <= expires then return {1} end                    -- never shorten

for seat in string.gmatch(redis.call('HGET', KEYS[3], 'seats'), '([^,]+)') do
  if redis.call('HGET', KEYS[2], seat) == ARGV[1] then
    redis.call('ZADD', KEYS[1], 'XX', newExpiry, seat)
  end
end
redis.call('HSET', KEYS[3], 'expires', newExpiry)
redis.call('PEXPIREAT', KEYS[3], newExpiry + tonumber(ARGV[3]))
if redis.call('GET', KEYS[4]) == ARGV[1] then redis.call('PEXPIREAT', KEYS[4], newExpiry) end
return {1}
