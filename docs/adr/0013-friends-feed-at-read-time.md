# ADR-0013: The friends feed is built when it is opened

**Status:** Accepted · 2026-11-09 · Story #78

## Context
People want to see what their friends have brewed and which beans they added. The feed shows brews and new beans of everyone I follow (accepted follows only), newest first, paged. Two ways to build it: **at read time** (merge the latest shared rows of each friend when the feed is asked for) or **ahead of time** with a change-feed Azure Function that writes a per-follower feed as friends save things.

## Decision
Build it **at read time** in the API (`FriendService.FeedAsync`, `GET /friends/feed`).

Why:
- It needs no new storage and no Azure Function, so it works and is tested today (Azure is not available yet, and a Function could only be written, not verified).
- It reuses the rules that already decide what a person may read (`VisibilityRules`, relationship and blocks), so there is no second copy of the privacy logic to keep in step. A removed, blocked or un-friended person simply drops out on the next load; a precomputed feed would need cleaning up.
- Cost is small at the size of this app: per followed friend it reads the newest rows of two tables, bounded (`FeedDepth` = 100 per person and table, so the feed shows recent activity, not all of history).

## How it works
- For each accepted follow, the friend's shared brews and beans are listed through the same `ListSharedAsync` as the friend profile, merged by time (a brew counts from when it was brewed, a bean from when it was added) and cut to the page.
- A brew carries the name of its bean, looked up through its bag in the friend's household, because bags are not shared.
- The app remembers the first page in memory, so the feed still opens when the network is gone. There is a Refresh button; pull-to-refresh can be added later without changing the API.

## When to revisit
When someone follows hundreds of people, or the feed becomes slow: then precompute it with a change-feed Function (one feed document per follower, trimmed to the latest N, rebuilt when a follow changes), keeping `FeedAsync` as the fallback and as the place the privacy rules are checked.
