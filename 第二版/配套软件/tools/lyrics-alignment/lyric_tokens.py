"""Keep display syllables separate from the text passed to the acoustic model."""
import re
import unicodedata


def syllables(text):
    # Mandarin characters are individual syllables; Latin words remain intact.
    return re.findall(r"[A-Za-zÀ-ÖØ-öø-ÿ]+(?:['’][A-Za-zÀ-ÖØ-öø-ÿ]+)*|[\u3400-\u9fff\uf900-\ufaff]|[^\W_]", text, re.UNICODE)


def spoken(token):
    # The model's English /ow/ is often skipped in Mandarin singing. Its
    # Mandarin equivalent has acoustic support; keep the original display text.
    return "哦" if token.casefold() == "oh" else token


def normalized(text):
    return unicodedata.normalize("NFKC", text).casefold()


def restore_tokens(expected, intervals):
    actual = [normalized(item.mark) for item in intervals]
    wanted = [normalized(spoken(token)) for token in expected]
    if actual != wanted:
        raise ValueError("模型输出与原歌词不一致，存在漏字或多字；未导入不完整结果："
                         + "/".join(expected))
    return list(zip(expected, intervals))
