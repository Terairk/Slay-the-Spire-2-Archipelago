# Continue multiplayer code learning on another machine

Use this document after checking out `experimental/multiple-singleplayer-saves`. Before teaching,
the LLM should inspect the checked-out revision and current source rather than assuming the revision
recorded here is still the branch head.

## Continuation prompt

> I am learning the Slay the Spire 2 Archipelago multiplayer implementation so I can navigate and
> present its finer code details. I already understand the high-level architecture. Teach from the
> current source, feature by feature, following complete vertical slices rather than reading files
> from top to bottom.
>
> Read `docs/learning/README.md` for the living route and use
> `docs/learning/multiplayer-diff-map.md` only as a historical syllabus. Treat
> `docs/design/replicated-card-reward-walkthrough.md` as a commit-specific historical explanation,
> not as the current card protocol.
>
> Keep the session read-only. I may add normal, production-style comments to the source for my own
> future reference. Preserve those comments, do not treat them as authoritative evidence, and do
> not review or critique them unless I explicitly ask.
>
> Resume with `ApRunData.PublishLocalProgress` in
> `client/StS2AP/Multiplayer/ApRunData.cs`. We have not yet completed this method. Use this concrete
> case: a non-host player's local AP progress changes and must be communicated to the host. Ask me
> to divide the method into admission checks, change calculation, message construction/sending, and
> local state advancement. Then ask me to explain:
>
> - what returns `true` without sending;
> - why the first publication has no delta;
> - which assignments happen after a successful send;
> - whether those assignments prove host acceptance or only successful transmission.
>
> Let me give the first interpretation. Then correct or extend it expression by expression using
> exact source references. Work through one connected method at a time; do not reveal the rest of
> the call chain in advance. For every section, distinguish the execution lane (local owner, host,
> every replica, or native synchronizer), whose state it represents, what is mutated, and whether
> the result is local, authoritative, replicated, or durable. Avoid compulsory quizzes, but ask for
> my interpretation before supplying the explanation.
>
> After `PublishLocalProgress`, follow one non-host update through
> `OnProgressDeltaReceived`, host validation and rebroadcast, replica storage, and eventual host
> checkpointing. Then use the feature order in `docs/learning/README.md` rather than starting over
> with a general architecture summary.

## State at handoff

- Branch: `experimental/multiple-singleplayer-saves`
- Revision when this handoff was written: `43b9a6b33c087836847928bf88cf114e0bf29892`
- Current lesson: progress synchronization
- First method: `ApRunData.PublishLocalProgress`
- Learner response still pending: the four-phase interpretation and the four questions above
- Source comments added by the learner are personal maintainership notes and are outside the lesson
  unless the learner asks to discuss them
