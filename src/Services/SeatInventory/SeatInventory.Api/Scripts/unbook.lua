-- Return cancelled seats to sale.
-- KEYS: 1 booked:{s}  2 seq:{s}     ARGV: seatIds
for i = 1, #ARGV do redis.call('SREM', KEYS[1], ARGV[i]) end
return redis.call('INCR', KEYS[2])
