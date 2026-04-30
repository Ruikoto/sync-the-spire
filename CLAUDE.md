# Repo guidelines for Claude

## Conventions for GitHub comments / PR descriptions

**Avoid `#<number>` notation:** in places that GitHub renders (issue comments, PR descriptions, commit message bodies, etc.), forms like `#1`, `#2` are auto-resolved as issue/PR references. They pollute the reference list of the target issue/PR and produce misleading backlinks.

**Use one of these instead:**

- Replace ordinals with letter-prefixed temporary IDs: `V-1`, `V-2`, `N-1`, `P-1`, etc. For example, change `#1`, `#2` in a review-finding list to `V-1`, `V-2` (V = vulnerability/verdict, N = note, P = problem — pick whatever fits, keep it consistent in context).
- If you really do mean to reference an issue/PR in this repo, `#<number>` is fine; this rule only applies when `#N` is being used as a list ordinal or internal index.
- For inline ordinals, prefer plain numbers with a period (`1.`, `2.`) or a markdown list. Don't write `#1.`, `#2.`.

**Bad → good:**

| Bad | Good |
|---|---|
| `#1 SaveConfig field missing` | `V-1 SaveConfig field missing` |
| `re #5 and #7` | `re V-5 and V-7` |
| `fixed #2, #3, #4` | `fixed V-2, V-3, V-4` |

**Scope:** anywhere GitHub renders Markdown — issue comments, PR comments, PR descriptions, commit messages. Local code comments and log output are not subject to this rule.
