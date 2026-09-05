#!/usr/bin/env python3
"""
Assembling the stream into one response — the one place where the bridge could lie unnoticed.

A truncated tool-call argument doesn't look like an error: it looks like the agent's
decision. That's why this uses recorded streams instead of stubs, and checks not "it didn't
crash" but the assembly itself.

Run: python3 -m unittest discover -s Tools/grokbridge
"""

import unittest

from grokbridge import assemble


def sse(*frames: str):
    """A stream exactly as HTTPResponse yields it: byte strings with a trailing newline."""
    return [(frame + "\n").encode("utf-8") for frame in frames]


class AssembleText(unittest.TestCase):
    def test_куски_текста_склеиваются_по_порядку(self):
        result = assemble(
            sse(
                'data: {"id":"x","choices":[{"delta":{"role":"assistant","content":"Реак"}}]}',
                'data: {"id":"x","choices":[{"delta":{"content":"тор "}}]}',
                'data: {"id":"x","choices":[{"delta":{"content":"в норме"},"finish_reason":"stop"}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )

        message = result["choices"][0]["message"]
        self.assertEqual(message["content"], "Реактор в норме")
        self.assertEqual(result["choices"][0]["finish_reason"], "stop")
        self.assertNotIn("tool_calls", message)
        self.assertEqual(result["model"], "grok-4.6")
        self.assertEqual(result["object"], "chat.completion")

    def test_размышления_не_попадают_в_реплику(self):
        # Merging the reasoning into the reply would turn the internal monologue into the
        # agent's in-game speech — i.e. a leak of an evil AI's plan straight into public chat.
        result = assemble(
            sse(
                'data: {"choices":[{"delta":{"reasoning_content":"надо соврать про реактор"}}]}',
                'data: {"choices":[{"delta":{"content":"Всё в порядке."},"finish_reason":"stop"}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )

        self.assertEqual(result["choices"][0]["message"]["content"], "Всё в порядке.")
        self.assertNotIn("соврать", str(result["choices"][0]["message"]))

    def test_комментарии_и_пустые_строки_пропускаются(self):
        # A leading colon sends keep-alive, so an intermediary doesn't close the connection.
        result = assemble(
            sse(
                ": keep-alive",
                "",
                'data: {"choices":[{"delta":{"content":"ок"},"finish_reason":"stop"}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )
        self.assertEqual(result["choices"][0]["message"]["content"], "ок")

    def test_битый_кадр_не_роняет_остальной_ответ(self):
        # A response missing one token beats no response at all: an agent's turn costs minutes.
        result = assemble(
            sse(
                'data: {"choices":[{"delta":{"content":"пол"}}]}',
                "data: {этонеjson",
                'data: {"choices":[{"delta":{"content":"овина"},"finish_reason":"stop"}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )
        self.assertEqual(result["choices"][0]["message"]["content"], "половина")


class AssembleToolCalls(unittest.TestCase):
    def test_аргументы_склеиваются_из_кусков(self):
        result = assemble(
            sse(
                'data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1",'
                '"function":{"name":"goto","arguments":""}}]}}]}',
                'data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\\"handle\\":"}}]}}]}',
                'data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\\"door-11\\"}"}}]}}]}',
                'data: {"choices":[{"finish_reason":"tool_calls","delta":{}}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )

        calls = result["choices"][0]["message"]["tool_calls"]
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0]["id"], "call_1")
        self.assertEqual(calls[0]["function"]["name"], "goto")
        self.assertEqual(calls[0]["function"]["arguments"], '{"handle":"door-11"}')

    def test_два_параллельных_вызова_не_перемешиваются(self):
        result = assemble(
            sse(
                'data: {"choices":[{"delta":{"tool_calls":['
                '{"index":0,"id":"a","function":{"name":"say","arguments":"{\\"t\\":\\"1\\"}"}},'
                '{"index":1,"id":"b","function":{"name":"hit","arguments":"{\\"t\\":"}}]}}]}',
                'data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"\\"2\\"}"}}]}}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )

        calls = result["choices"][0]["message"]["tool_calls"]
        self.assertEqual([c["function"]["name"] for c in calls], ["say", "hit"])
        self.assertEqual(calls[0]["function"]["arguments"], '{"t":"1"}')
        self.assertEqual(calls[1]["function"]["arguments"], '{"t":"2"}')
        self.assertEqual(result["choices"][0]["finish_reason"], "tool_calls")

    def test_имя_повторённое_в_каждом_кадре_не_задваивается(self):
        # Not hypothetical: providers that send the whole call in every frame do exist, and
        # concatenation without a guard would produce a tool named "movemovemove" that isn't
        # in the list.
        result = assemble(
            sse(
                'data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c",'
                '"function":{"name":"move","arguments":"{\\"d\\":"}}]}}]}',
                'data: {"choices":[{"delta":{"tool_calls":[{"index":0,'
                '"function":{"name":"move","arguments":"\\"north\\"}"}}]}}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )

        calls = result["choices"][0]["message"]["tool_calls"]
        self.assertEqual(calls[0]["function"]["name"], "move")
        self.assertEqual(calls[0]["function"]["arguments"], '{"d":"north"}')

    def test_вызов_без_индекса_ключуется_по_id(self):
        result = assemble(
            sse(
                'data: {"choices":[{"delta":{"tool_calls":[{"id":"z",'
                '"function":{"name":"look","arguments":"{}"}}]}}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )

        calls = result["choices"][0]["message"]["tool_calls"]
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0]["id"], "z")
        self.assertEqual(calls[0]["function"]["arguments"], "{}")

    def test_finish_reason_подставляется_когда_вендор_промолчал(self):
        result = assemble(
            sse(
                'data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"q",'
                '"function":{"name":"noop","arguments":"{}"}}]}}]}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )
        self.assertEqual(result["choices"][0]["finish_reason"], "tool_calls")

    def test_пустой_поток_даёт_валидный_пустой_ответ(self):
        # The agent survives an empty response — it gets nudged past it; but a KeyError in the
        # bridge would look like a 502 and needlessly push the chain to the next profile.
        result = assemble(sse("data: [DONE]"), "grok-4.6")
        self.assertEqual(result["choices"][0]["message"]["content"], "")
        self.assertEqual(result["choices"][0]["finish_reason"], "stop")


class AssembleUsage(unittest.TestCase):
    def test_расход_берётся_из_последнего_кадра(self):
        # Without usage the `aiagent cost` counter shows zeros, and there's no way left to
        # figure out where the weekly Grok quota went.
        result = assemble(
            sse(
                'data: {"choices":[{"delta":{"content":"да"},"finish_reason":"stop"}]}',
                'data: {"choices":[],"usage":{"prompt_tokens":52700,"completion_tokens":310,'
                '"prompt_tokens_details":{"cached_tokens":51000},'
                '"completion_tokens_details":{"reasoning_tokens":215}}}',
                "data: [DONE]",
            ),
            "grok-4.6",
        )

        self.assertEqual(result["usage"]["prompt_tokens"], 52700)
        self.assertEqual(result["usage"]["prompt_tokens_details"]["cached_tokens"], 51000)
        self.assertEqual(result["usage"]["completion_tokens_details"]["reasoning_tokens"], 215)

    def test_без_usage_поле_не_выдумывается(self):
        # A confident zero is worse than absence: it can't be told apart from a cheap turn.
        result = assemble(
            sse('data: {"choices":[{"delta":{"content":"да"},"finish_reason":"stop"}]}', "data: [DONE]"),
            "grok-4.6",
        )
        self.assertNotIn("usage", result)


if __name__ == "__main__":
    unittest.main()
