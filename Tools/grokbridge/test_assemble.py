#!/usr/bin/env python3
"""
Сборка потока в один ответ — единственное место, где мост может соврать незаметно.

Оборванный аргумент вызова инструмента не выглядит ошибкой: он выглядит как решение агента.
Поэтому здесь записанные потоки, а не заглушки, и проверяется не «не упало», а склейка.

Запуск: python3 -m unittest discover -s Tools/grokbridge
"""

import unittest

from grokbridge import assemble


def sse(*frames: str):
    """Поток, как его отдаёт HTTPResponse: байтовые строки с переводом строки."""
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
        # Склейка размышлений с ответом превратила бы внутренний монолог в речь агента в игре —
        # то есть в утечку замысла злого ИИ прямо в общий эфир.
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
        # Двоеточием шлют keep-alive, чтобы соединение не закрыл посредник.
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
        # Ответ без одного токена лучше отсутствующего ответа: ход агента стоит минуты.
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
        # Не гипотетика: провайдеры, шлющие вызов целиком в каждом кадре, встречаются, и склейка
        # без защиты дала бы инструмент «movemovemove», которого в списке нет.
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
        # Пустой ответ агент переживает — он его отобьёт нудджем; а вот KeyError в мосте
        # выглядел бы как 502 и увёл бы цепочку на следующий профиль без нужды.
        result = assemble(sse("data: [DONE]"), "grok-4.6")
        self.assertEqual(result["choices"][0]["message"]["content"], "")
        self.assertEqual(result["choices"][0]["finish_reason"], "stop")


class AssembleUsage(unittest.TestCase):
    def test_расход_берётся_из_последнего_кадра(self):
        # Без usage счётчик `aiagent cost` показывает нули, и понять, куда ушла недельная квота
        # Grok, становится нечем.
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
        # Уверенный ноль хуже отсутствия: по нему нельзя отличить дешёвый ход от неизвестного.
        result = assemble(
            sse('data: {"choices":[{"delta":{"content":"да"},"finish_reason":"stop"}]}', "data: [DONE]"),
            "grok-4.6",
        )
        self.assertNotIn("usage", result)


if __name__ == "__main__":
    unittest.main()
