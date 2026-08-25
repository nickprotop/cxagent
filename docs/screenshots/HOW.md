# Taking these

The shots are `tmux capture-pane` output rendered to PNG, not photographs — so the colours are the
ones cxagent emitted and the glyph metrics are the renderer's rather than whatever terminal took the
picture.

```bash
tmux new-session -d -s shot -x 132 -y 43 -c /path/to/repo cxagent
# ... drive the session to the state you want ...
tmux capture-pane -t shot -p -e > shot.ansi     # -e keeps the escape sequences
python3 render.py shot.ansi shot.png
```

`132x43` matches the existing images. `render.py` needs Pillow and DejaVu Sans Mono, and understands
the 16 base colours, the 256-colour cube and truecolour — which is all cxagent uses.

**Drive a real session.** Every image here is a state cxagent actually reached; the mistakes are in
the pictures because they are what the session did. A staged screenshot is a claim rather than
evidence, and this walkthrough is the evidence.
