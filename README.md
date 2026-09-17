The Does the Dog Die client is a community lead API client for
https://www.doesthedogdie.com/, with a focus on managing the limited request 
rate available on the free tier of the API.  This project is not affiliated
with DtDD but *would* appreciate access to the model files or API definitions,
*hint hint*.

# We are under construction, please pardon the mess
This document is the first I'm writing, before code, before hard requirements,
all that good stuff.  Will it muddy the git history?  Yes.

# The whatfor

## Why make client library, which has one intended use-case?
Separate from this specific client, I'm working on a Jellyfin plugin to add
DtDD metadata to media entries.  That's all I have in the works, but I want
a separation between the code bases.  If someone else wants to use this 
elsewhere, hooray.  Otherwise, it's to allow me to get the DtDD side sorted
without dealing with the overhead of Jellyfin.

## The overhead
The new changes that DtDD is in the progress of making for their API include
restrictions on the number of requests per month, a restrictive enough number
that I feel it's worth taking an aggressive, persistent caching approach to 
this.  For anything that has existed for some amount of time, i.e. a movie
from six months ago, the probability of any data become stale is quite low,
provided new trigger categories aren't added (they can be, of course, but 
not daily).  Additionally, we can have a certain level of confidence in a 
rating if we leverage probability.  

### If you would be so kind as to indulge a tangent into some math
Let us consider Does the Dog Die to answer, for a given trigger, what is 
the probability that a given trigger is observed by an audience member of a 
given work?  Critically, it does not answer whether the work does definitively
contain triggering material, only if it is likely to be perceived.  We will 
call the variable representing the probability of triggering material being
seen T, and therefor it's probability is P(T=1).  This is a clear example of a
Bernoulli distribution.  The mean of this distribution is rather trivial to 
calculate, but we would like to answer "how certain can we be that a given
trigger is present in a given work"?  To generate confidence intervals, we will
need to utilize the conjugate prior distribution, which in this case is the 
Beta distribution.  This allows us to take a number of positive and negative
observations (the trigger was observed by a user, the trigger was not observed
by a user) and create a probability density function of the underlying probability
that a trigger can be observed.  

# This feels a bit larger than just an API client
Yeah, here goes some more: given the rate limits on the API and a general 
expectation of burstiness trying to get data *from* the API (like a Jellyfin
client refreshing library metadata) we need prioritize what data we grab and 
leave space for certain items.  A reasonably sized media library might easily
contain 1,000 items, when a show is considered an item but a season or episode 
is not.  A show will have a few seasons, at least one but two to five is 
within a pretty normal range.  Each season will have between twelve and 
twenty-four.  Based on that, a show could easily have over a hundred episodes.
I need to confirm this, but I believe that in DtDD a show aggregates votes in 
some way for episodes>seasons>show to make the higher-level entry accurate as 
new episodes are released.  An aside, a currently broadcast show will not 
immediately have accurate information available, so even if you get a fresh 
entry for that episode it may need to be refreshed multiple times before 
confidence is reached for that episode.

## That's a lot of preamble, here's some thoughts on design
The thin API layer, which, christ, maybe should be its own package, is pretty 
simple.  The following are the listed data model types:
* ItemType
* Item
* TopicItemStat
* Rating 
* Topic 
* TopicCategory 
* TopicSuperCategory 
Beyond the data model (and not paying much heed to the relations between these 
data types) there are headers in each response for the limits for the API key 
used for the minute and the month its used, as well as the remaining requests 
for that minute and month.  When rate limits are exceeded, a 429 response is 
served with a header with a safe retry timeout.

Skipping from one end to another, we need a client which can be called to get 
that data; in the simplest case, this client should function across threads and 
handle timeouts with the normal timeout token architecture that I frankly don't 
understand particularly well, but that's okay.  And if we allow ourselves a 
little extra complexity, as a treat, we can queue up the requests made to DtDD 
and quickly run out of free tier requets.

Somewhere in the middle, we need to take the requests from the actual client, 
form a work queue, and do something with that.  I literally just talked about 
a cacheless solution, and that will probably come about as a simple first 
implementation.  For the actual cache, if there's a cache hit then hooray, we 
just hand the client the data out of the cache.  If the data isn't in the cache, 
then we should add the request to a work queue.  We can anticipate if the 
request will be served in anything approximating a reasonable amount of time, 
and if so we just process it as described for the cacheless mode, but obviously 
caching any results returned from the API.  If, for instance, the position in 
the work queue is such that it won't be fetched this month, we should just 
return an error stating that and let the application handle what they do with 
that information.  

The cache should persist over a long period of time, and a cache entry which 
is invalidated due to age should still be returned, but with a little note 
(i.e. a specific flag indicating this to the application to handle).  The rate 
limits, while reasonable, do prevent you from going hog-wild shooting off 
requests; that's why they're there.  But a lot of information is going to remain 
valid for a long time, even if it would be a candidate for re-checking.  The 
heuristic for the queueing is going to need to take a lot of things into account.

* Entries which have not ever been in the cache should be prioritized over items 
  which are in the cache, though an exception *may* be individual episodes.
* Movies and Shows should be prioritized at a higher level than seasons of shows, 
  which should be at a higher level than individual episodes.
* A weighting scheme needs to be determined for items which are populated in the 
  cache.
  * Items which are not recently released, and which have a high level of 
    confidence should be the lowest piority to update.  These are likely not to 
    change over time.
  * An item which has a high level of confidence, and is recently released 
    (including shows with recently released episodes), should be at a low priority.
    While the data we have is likely correct, 
  * An item which has had a release after it was last retrieved should be 
    considered to have a lower confidence.
  * Topics should have a weight assigned to them, allowing topics to be ignored,
    considered low-to-high priority, and recieve a default priority when a new 
    topic is added.
* Topics, TopicCategories, and TopicSuperCategories should be updated at a 
  regular interval to ensure changes are caught; this means these will be at 
  the highest priority with a simple TTL.

# Conclusions
There's more thoughts to be had, but it's already late as it is.
