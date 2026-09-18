# Training source notices

These files accompany the experimental candidate. Source archive revisions and
checksums are in `../sources.json` and `../public-sources.json`. Model training
does not establish that generated statements are factual.

- **WikiText-103 raw:** Stephen Merity and collaborators, Salesforce Research,
  and the Wikipedia contributors identified by the source articles. The pinned
  dataset card is `wikitext.md`; it retains the CC BY-SA and GFDL notices.
- **TinyStories V2 GPT-4:** Ronen Eldan and Yuanzhi Li. Synthetic stories generated
  with GPT-4. The pinned card is `tinystories.md`; the CDLA Sharing 1.0 license is
  `tinystories-license.txt`.
- **OpenAssistant Conversations 1 and 2:** OpenAssistant contributors. The pinned
  OASST2 card is `openassistant.md`; Apache License 2.0 is retained in
  `openassistant-license.txt`.
- **Blended Skill Talk:** Smith et al., Facebook AI Research, and conversation
  contributors. The official task documentation is `blended-skill-talk.md`;
  CC BY 4.0 is retained in `blended-skill-talk-license.txt`.

This experiment filters source rows, restores original conversational casing,
serializes explicit message roles, and tokenizes text. It does not redistribute
the raw corpora in Git. Authored NPC episodes are separate project material.
`notice-sources.json` records retrieval URLs and content hashes for supplementary
notices. The exact pinned language cards are bound by the source manifest.
