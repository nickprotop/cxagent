# calculator

Arithmetic a model can trust.

A language model approximates arithmetic rather than computing it, and a calculation spread over
several turns goes wrong somewhere in the middle — confidently. This does the same work exactly, in
one call.

## The one tool

`calc_eval(expression)` — give it the whole calculation, get the answer.

```
(1847 * 0.0325) / 12   ->  5.00229166666667
sqrt(144) + 2^10       ->  1036
```

**Compose the whole thing into one expression.** Three calls with reasoning between them is the
problem this solves, not the way to use it.

## What it knows

`sqrt abs ceiling floor round truncate max min sin cos tan asin log10 loge logn`

**`^` is exponentiation**, and right-associative — `2^3^2` is 512.

**There is no `log`.** It is `log10`, `loge`, or `logn(value, base)` — which is one keystroke more
and never ambiguous about the base.

## What it will not do

**Anything that is not a number.** `1/0` and an overflow are refused rather than answered with `∞`,
because a symbol presented as a result is something you might carry into the next step.

**`random()`.** A calculator that answers differently for the same input cannot be reasoned about.

**Exact decimal arithmetic.** Results are double-precision. Fine for engineering and statistics; not
the tool for money.

## What it does not need

No permission prompt. The tool reads nothing, writes nothing, starts no process and opens no
connection — it is a function from a string to a number, so there is nothing to ask about.

Approval is still asked once for the plugin itself, as for any plugin: that prompt is about trusting
the binary, not about what the tool does.
