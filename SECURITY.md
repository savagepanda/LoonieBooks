# Security

LoonieBooks holds people's financial records, so security reports are very welcome.

## Reporting a problem

Please **don't open a public issue** for a security problem. Report it privately instead: on GitHub,
open the repository's **Security** tab and choose **Report a vulnerability**. Include what you found,
how to reproduce it, and what an attacker could do with it.

LoonieBooks is maintained by one person in their spare time. You'll get an answer as soon as I can
manage, usually within a week, and a fix in a new release once the problem is confirmed. I'm happy
to credit you in the release notes if you'd like.

## Which versions

Only the latest release is fixed. Upgrading is just unzipping the new one (see the README's
*Desktop app*), and the browser version updates itself.

## What's in scope

The design is described in the README (*Security model*). Reports are especially useful about:

- Reading or changing someone else's ledger on a self-hosted server: the boundary between the web
  host and the API (`InternalApiToken`), ownership filtering, or server storage (`PremiumLedgerFilter`).
- The desktop app's network sharing: the household passcode, its cookie and throttling, the
  certificates, and requests that act as the desktop owner (`LanRequests`).
- *Send from phone*: the one-time links and the receipt relay.
- Anything that sends a ledger, or part of one, somewhere the person didn't choose.

Not in scope: problems that need an attacker to already control the person's computer or browser,
and the hosting of a site that someone runs themselves.
