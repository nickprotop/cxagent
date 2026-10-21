# worktree

Sandboxes for work that should not land until someone says so: a git worktree per task, merged or
thrown away whole.

A sub-agent edits the files you are working on, live, and you cannot take back an edit when only half
of it was right. A worktree is a second checkout of the repository on its own branch. An agent
working there changes nothing you see until a merge brings its work in, and a discard leaves no trace.

## How it is used

```
worktree_create  name: "fix-login"
  → Created worktree 'fix-login' at .worktrees/fix-login, on branch worktree/fix-login, from 3f2a9c1 (HEAD).
    To have a sub-agent work there, spawn it with root ".worktrees/fix-login". …

agent  root: ".worktrees/fix-login"  prompt: "Fix the login timeout …"

worktree_diff   name: "fix-login"      → what the agent changed, new files included
worktree_merge  name: "fix-login"      → asks you, then merges into your current branch
worktree_discard name: "fix-login"     → asks you, then throws it away
```

| tool | what it does | asks you each call |
| --- | --- | --- |
| `worktree_create` | A new checkout on branch `worktree/<name>`, in `.worktrees/<name>` inside your folder | no |
| `worktree_list` | The worktrees made here, and how much unmerged work each holds | no |
| `worktree_diff` | What a worktree changed since it was made, committed or not, new files included | no |
| `worktree_merge` | Commits what the worktree has not committed, merges its branch into yours, and removes it | **yes**, naming the worktree |
| `worktree_discard` | Removes the folder and deletes the branch, with any unmerged work | **yes**, and never offers "Always" |

Like any plugin's tools, each is also asked about once per folder before its first use, and "Always
allow" answers that for good. The column is about the prompt the plugin adds on top of that.

## What to expect

**The worktree is inside your folder.** That is what lets a sub-agent's `root` point at it, since a
root must be inside the parent agent's folder. The folder is added to the repository's
`.git/info/exclude`, which belongs to your clone and is never committed. So `git status` stays clean,
searches don't find every file twice, and your `.gitignore` is not touched.

**A worktree starts from the last commit.** Uncommitted changes in your folder are not in it, and
`worktree_create` says so when you have some.

**A merge that conflicts changes nothing.** It is undone on the spot. Your checkout is left as it was,
not half-merged with conflict markers in your files, and the worktree keeps its work. Bring your
branch into the worktree, resolve the conflicts there, and merge again.

**A merge is a normal git merge.** It fast-forwards when your branch has not moved, and makes a merge
commit when it has, following your git configuration. Uncommitted work in the worktree is first
committed there, with the `message` you pass or `Work from worktree '<name>'`.

**Only worktrees in `.worktrees/` are this plugin's.** Worktrees you made yourself elsewhere are not
listed, merged or removed. Worktrees also outlive the session: an unmerged one is still there next
time, for `worktree_list` to show.

**From the main checkout only.** Called from inside a worktree, every tool refuses. A sub-agent working
in a worktree cannot make, merge or discard worktrees of its own.

## What it needs

`git` on `PATH`, or the path set as `git` in its settings. Every call runs a few git commands, which
have exited before the call returns. Each one is registered with cxagent, so a crash cannot leave one
running.

## Configuring it

```json
"plugins": {
  "worktree": {
    "file": "worktree/worktree.dll",
    "settings": { "folder": ".worktrees", "branchPrefix": "worktree/", "git": "git" }
  }
}
```

Every setting is optional and the values shown are the defaults. `folder` must be inside your folder,
and not inside `.git`, `.cxagent` or another folder whose files can run code, because a sub-agent
cannot be rooted there.
