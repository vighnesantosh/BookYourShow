-- Release a hold's seats (user cancel, payment failure, or timeout).
-- KEYS: 1 holds:{s}  2 owner:{s}  3 hold:{s}:{h}  4 userhold:{s}:{u}  5 seq:{s}
-- ARGV: 1 holdId  2 onlyIfExpired (0/1)  3 nowMs
-- Returns {1, seq, freedSeat...} | {0, 'NOT_FOUND'} | {0, 'CONFIRMED'} | {0, 'ALREADY_RELEASED', seat...} | {0, 'NOT_EXPIRED', expiresMs}
if redis.call('EXISTS', KEYS[3]) == 0 then return {0, 'NOT_FOUND'} end

local state = redis.call('HGET', KEYS[3], 'state')
local seatsCsv = redis.call('HGET', KEYS[3], 'seats')
local seats = {}
for seat in string.gmatch(seatsCsv, '([^,]+)') do seats[#seats + 1] = seat end

if state == 'CONFIRMED' then return {0, 'CONFIRMED'} end
if state == 'RELEASED' then
  local r = {0, 'ALREADY_RELEASED'}
  for _, s in ipairs(seats) do r[#r + 1] = s end
  return r
end

local expires = tonumber(redis.call('HGET', KEYS[3], 'expires'))
if ARGV[2] == '1' and expires > tonumber(ARGV[3]) then return {0, 'NOT_EXPIRED', tostring(expires)} end

local result = {1, 0}
for _, seat in ipairs(seats) do
  if redis.call('HGET', KEYS[2], seat) == ARGV[1] then       -- only seats this hold still owns
    redis.call('ZREM', KEYS[1], seat)
    redis.call('HDEL', KEYS[2], seat)
    result[#result + 1] = seat                                -- report only seats actually freed
  end
end

redis.call('HSET', KEYS[3], 'state', 'RELEASED')
if redis.call('GET', KEYS[4]) == ARGV[1] then redis.call('DEL', KEYS[4]) end
result[2] = redis.call('INCR', KEYS[5])
return result
