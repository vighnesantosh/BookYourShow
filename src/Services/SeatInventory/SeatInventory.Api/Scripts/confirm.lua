-- Apply a confirmed sale (after Aurora's unique constraint accepted it).
-- The database is the authority, so seats are marked sold unconditionally.
-- KEYS: 1 holds:{s}  2 owner:{s}  3 booked:{s}  4 hold:{s}:{h}  5 userhold:{s}:{u}  6 seq:{s}
-- ARGV: 1 holdId  2.. seatIds
-- Returns {1, seq}
for i = 2, #ARGV do
  redis.call('SADD', KEYS[3], ARGV[i])
  redis.call('ZREM', KEYS[1], ARGV[i])
  redis.call('HDEL', KEYS[2], ARGV[i])
end
if redis.call('EXISTS', KEYS[4]) == 1 then redis.call('HSET', KEYS[4], 'state', 'CONFIRMED') end
if redis.call('GET', KEYS[5]) == ARGV[1] then redis.call('DEL', KEYS[5]) end
return {1, redis.call('INCR', KEYS[6])}
