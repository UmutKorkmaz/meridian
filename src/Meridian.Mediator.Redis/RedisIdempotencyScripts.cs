namespace Meridian.Mediator.Redis;

internal static class RedisIdempotencyScripts
{
    // One key; validate the complete finite record before every mutation. TIME is the transition authority.
    internal const string Transition = """
local k=KEYS[1]
local op=ARGV[1]
local owner=ARGV[2]
local contract=ARGV[3]
local schema=ARGV[4]
local leaseMs=ARGV[5]
local retention=ARGV[6]
local payload=ARGV[7]
local maximum=tonumber(ARGV[8])
local function bad() error('MERIDIAN_INVALID_RECORD',0) end
local function integer(s,max)
 if not s or not string.match(s,'^[1-9][0-9]*$') or #s>16 then bad() end
 local n=tonumber(s); if not n or n>max then bad() end; return n
end
local function id(s)
 if not s or #s<1 or #s>128 or string.find(s,'[^A-Za-z0-9_.-]') then bad() end
end
local function token(s)
 if not s or #s~=32 or string.find(s,'[^0-9a-f]') then bad() end
end
integer(leaseMs,2147483647);integer(retention,2147483647)
local t=redis.call('TIME');local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
local kind=redis.call('TYPE',k).ok
if kind=='none' then
 if op=='read' then return {'absent'} end
 if op=='renew' then return {'lost'} end
 if op~='claim' and op~='store' then return {'lost'} end
 token(owner);id(contract);integer(schema,2147483647)
 if op=='store' and (#payload<11 or #payload>maximum) then bad() end
 local untilMs=now+tonumber(leaseMs)
 redis.call('HSET',k,'v','1','state',op=='claim' and 'P' or 'C','owner',owner,'contract',contract,'schema',schema,'leaseMs',leaseMs,'leaseUntilMs',string.format('%.0f',untilMs))
 if op=='store' then redis.call('HSET',k,'payload',payload);redis.call('PEXPIRE',k,retention);return {'ok'} end
 return {'claimed',owner,contract,schema,string.format('%.0f',now),string.format('%.0f',untilMs)}
end
if kind~='hash' then bad() end
local r=redis.call('HMGET',k,'v','state','owner','contract','schema','leaseMs','leaseUntilMs','payload')
local state=r[2]
if r[1]~='1' or (state~='P' and state~='C') then bad() end
token(r[3]);id(r[4]);integer(r[5],2147483647);integer(r[6],2147483647)
local untilMs=integer(r[7],9007199254740991)
local ttl=redis.call('PTTL',k)
if state=='P' then
 if redis.call('HLEN',k)~=7 or r[8]~=false or ttl~=-1 then bad() end
else
 if redis.call('HLEN',k)~=8 or not r[8] or #r[8]<11 or #r[8]>maximum or ttl<1 then bad() end
end
if contract~='' and (r[4]~=contract or r[5]~=schema) then error('MERIDIAN_CONTRACT_MISMATCH',0) end
if op=='read' or op=='claim' then
 if state=='C' then return {'completed',r[3],r[4],r[5],r[8]} end
 return {now<untilMs and 'inflight' or 'unknown',r[3],r[4],r[5],r[7]}
end
if op=='store' then return {'lost'} end
if r[3]~=owner then return {'lost'} end
if state=='C' then
 if (op=='complete' or op=='reconcile-complete') and r[8]==payload then return {'ok'} end
 return {'lost'}
end
local reconciliation=op=='reconcile-complete' or op=='reconcile-release'
if reconciliation then
 if now<untilMs or r[7]~=ARGV[9] then return {'lost'} end
elseif now>=untilMs then return {'lost'} end
if op=='renew' then
 redis.call('HSET',k,'leaseUntilMs',string.format('%.0f',now+tonumber(r[6])));return {'ok'}
end
if op=='release' or op=='reconcile-release' then redis.call('DEL',k);return {'ok'} end
if op=='complete' or op=='reconcile-complete' then
 if #payload<11 or #payload>maximum then bad() end
 redis.call('HSET',k,'state','C','payload',payload);redis.call('PEXPIRE',k,retention);return {'ok'}
end
bad()
""";
}
