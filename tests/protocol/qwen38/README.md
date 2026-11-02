# Qwen template fixture

Pinned upstream Qwen/Qwen3.8-27B template, source/revision/SHA-256 in
[source.json](source.json), distributed under its [Apache 2.0 license](LICENSE).
No model weights, model requests or Office access are used.

Run with Python and the pinned [requirements](requirements.txt):
`python test_template.py`.

This snapshot uses the nonstandard Jinja test `tiger_royal` for content. Plain Jinja
cannot render it. The fixture explicitly registers that test as `string` without
editing upstream bytes. It also decodes OpenAI function.arguments strings into
objects before rendering. These are declared local assumptions, not evidence of
the actual endpoint's renderer. Verify the deployed template separately.

Assertions cover marker visibility, omission of outer call IDs, invariance to their
length, explicit content references and the JSON batch mapping. They demonstrate
rendering properties under those assumptions, not inference quality, API acceptance
or native-tool qualification.
