-- Consistent availability snapshot for the seat map.
-- KEYS: 1 holds:{s}  2 booked:{s}  3 seq:{s}     ARGV: 1 nowMs
-- Returns {seq, {booked...}, {held...}}
local seq = tonumber(redis.call('GET', KEYS[3]) or '0')
return {seq, redis.call('SMEMBERS', KEYS[2]), redis.call('ZRANGEBYSCORE', KEYS[1], '(' .. ARGV[1], '+inf')}
