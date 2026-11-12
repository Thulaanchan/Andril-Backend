# Security Acceptance Checklist (Unimplemented)

- Authenticate each protected request and validate active module identity in the API and data access layer.
- Enforce Social 18+ opt-in and mutual-match gating for match-based communication.
- Reject non-null MoodTone in Learning/Hobbies; Social messages may optionally select a valid tone.
- Prevent cross-module identity/media/profile/diary/AI-context correlations.
- Check ownership/roles for groups and Admin moderation; never trust UI-only restrictions.
- Restrict private media, secrets, database credentials and privileged Supabase keys to protected server components.
- Atomically and idempotently award/reverse coin, XP and Flight Point transactions.
- Require fresh camera evidence, location validation and Admin approval before Map display/rewards.
- Audit sensitive actions without exposing private content in ordinary logs.

**This ZIP only defines intended boundaries. It does not implement these protections.**
