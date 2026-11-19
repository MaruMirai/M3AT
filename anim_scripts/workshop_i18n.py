"""Workshop description translations, kept line by line and uploaded to Steam.

The English description (workshop_description_short_bold.txt at the repository's
root) is the only text edited by hand. Each language has a translation memory,
workshop_i18n/<language>.json: every English line that has words in it, mapped
to its translation ("" to leave the line out there). A line with no words (blank, [list], [hr][/hr]) is copied as
it is. Changing one English line leaves only that line untranslated, so the
rest of every translation stays exactly as it was, hand fixes included.

    python3 anim_scripts/workshop_i18n.py sync     # add the new English lines to every memory as null, drop lines gone from the English
    python3 anim_scripts/workshop_i18n.py todo     # list the lines each language still needs
    python3 anim_scripts/workshop_i18n.py build    # write workshop_i18n/out/<language>.txt (fails while any line is null)
    python3 anim_scripts/workshop_i18n.py upload [--dry] [--only lang,...] [--no-english] [--force]

upload builds, then sets each language's title and description on the Workshop
item through the Steamworks API (RimWorld's own libsteam_api.so, Steam running,
the game not needed), the English one as the item's default. Before that it
fetches the live English description and stops if it differs from the last one
this script uploaded (workshop_i18n/uploaded_english.txt), as then it was edited
on the website and uploading would lose that; --force uploads anyway. With no
change note given, Steam adds no change note entry for a text-only update.
"""

import ctypes
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, 'workshop_description_short_bold.txt')
FOLDER = os.path.join(ROOT, 'workshop_i18n')
OUT = os.path.join(FOLDER, 'out')
UPLOADED = os.path.join(FOLDER, 'uploaded_english.txt')
ITEM_FILE = os.path.join(ROOT, "M3AT: Maru's 3D Animated Toons", 'About', 'PublishedFileId.txt')
STEAM_API = os.path.expanduser('~/.local/share/Steam/steamapps/common/RimWorld/libsteam_api.so')
APP_ID = 294100

# The title is the mod's name in every language.
TITLE = "M3AT: Maru's 3D Animated Toons (IsoRim)"

# Steam's language codes for the API, and the memory files' names.
LANGUAGES = ['schinese', 'tchinese', 'russian', 'brazilian', 'koreana', 'japanese', 'latam']

# Steam's limit on a description (k_cchPublishedDocumentDescriptionMax), in UTF-8
# bytes: a Russian description of 7931 characters (11274 bytes) was refused with
# EResult 8 while a Chinese one of 7722 bytes went through. So Cyrillic, Korean
# and Japanese have to be put more briefly than the English; a translation of ""
# leaves that line out of its language.
DESCRIPTION_MOST = 8000


def source_lines():
    with open(SOURCE, encoding='utf-8') as f:
        return f.read().replace('\r', '').rstrip('\n').split('\n')


def has_words(line):
    """Whether a line has text to translate once its tags and links are taken out."""
    bare = re.sub(r'\[/?[a-z0-9*]+(=[^\]]*)?\]', '', line)
    bare = re.sub(r'https?://\S+', '', bare)
    return re.search(r'\w', bare) is not None


def tags(line):
    """A line's BBCode tags and links, which a translation must keep (in any order)."""
    return sorted(re.findall(r'\[/?[a-z*0-9]+(?:=[^\]]*)?\]', line))


def memory_path(lang):
    return os.path.join(FOLDER, lang + '.json')


def load_memory(lang):
    path = memory_path(lang)
    if not os.path.exists(path):
        return {}
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def save_memory(lang, memory):
    os.makedirs(FOLDER, exist_ok=True)
    with open(memory_path(lang), 'w', encoding='utf-8') as f:
        json.dump(memory, f, ensure_ascii=False, indent=1)
        f.write('\n')


def sync():
    wanted = [line for line in source_lines() if has_words(line)]
    for lang in LANGUAGES:
        memory = load_memory(lang)
        kept = {line: memory.get(line) for line in dict.fromkeys(wanted)}
        dropped = [line for line in memory if line not in kept]
        save_memory(lang, kept)
        missing = sum(1 for v in kept.values() if v is None)
        print(f'{lang}: {missing} to translate, {len(dropped)} dropped')


def todo():
    total = 0
    for lang in LANGUAGES:
        missing = [line for line, text in load_memory(lang).items() if text is None]
        total += len(missing)
        if missing:
            print(f'== {lang} ({len(missing)})')
            for line in missing:
                print('  ' + line)
    if total == 0:
        print('Every language is complete.')


def build():
    lines = source_lines()
    texts = {}
    problems = []
    for lang in LANGUAGES:
        memory = load_memory(lang)
        out = []
        for line in lines:
            if not has_words(line):
                out.append(line)
            elif memory.get(line) is None:
                problems.append(f'{lang}: no translation for: {line}')
                out.append(line)
            elif memory[line] != '':
                if tags(memory[line]) != tags(line):
                    problems.append(f'{lang}: tags or links differ from the English in: {memory[line]}')
                out.append(memory[line])
        text = '\n'.join(out) + '\n'
        texts[lang] = text
        if len(text.encode()) > DESCRIPTION_MOST:
            problems.append(f'{lang}: {len(text.encode())} bytes, over Steam\'s {DESCRIPTION_MOST}')
    if problems:
        print('\n'.join(problems))
        print('Run sync, fill in the nulls in workshop_i18n/<language>.json, then build again.')
        sys.exit(1)
    os.makedirs(OUT, exist_ok=True)
    for lang, text in texts.items():
        with open(os.path.join(OUT, lang + '.txt'), 'w', encoding='utf-8') as f:
            f.write(text)
        print(f'{lang}: {len(text)} characters, {len(text.encode())} bytes')
    return texts


def item_id():
    with open(ITEM_FILE) as f:
        return int(f.read().strip())


def live_english(item):
    data = urllib.parse.urlencode({'itemcount': 1, 'publishedfileids[0]': item}).encode()
    url = 'https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/'
    with urllib.request.urlopen(url, data, timeout=30) as reply:
        details = json.load(reply)['response']['publishedfiledetails'][0]
    return details['description'].replace('\r', '')


class SubmitResult(ctypes.Structure):
    # SubmitItemUpdateResult_t, packed to 4 on Linux.
    _pack_ = 4
    _fields_ = [('result', ctypes.c_int32), ('needs_agreement', ctypes.c_bool), ('item', ctypes.c_uint64)]


SUBMIT_CALLBACK = 3404


class Steam:
    def __init__(self):
        os.environ['SteamAppId'] = str(APP_ID)
        os.environ['SteamGameId'] = str(APP_ID)
        api = ctypes.CDLL(STEAM_API)
        error = ctypes.create_string_buffer(1024)
        api.SteamAPI_InitFlat.argtypes = [ctypes.c_char_p]
        api.SteamAPI_InitFlat.restype = ctypes.c_int
        if api.SteamAPI_InitFlat(error) != 0:
            raise SystemExit('Steam would not start its API (is Steam running and logged in?): '
                             + error.value.decode(errors='replace'))
        api.SteamAPI_SteamUGC_v021.restype = ctypes.c_void_p
        api.SteamAPI_SteamUtils_v010.restype = ctypes.c_void_p
        self.ugc = api.SteamAPI_SteamUGC_v021()
        self.utils = api.SteamAPI_SteamUtils_v010()
        api.SteamAPI_ISteamUGC_StartItemUpdate.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint64]
        api.SteamAPI_ISteamUGC_StartItemUpdate.restype = ctypes.c_uint64
        for name in ('SetItemUpdateLanguage', 'SetItemTitle', 'SetItemDescription'):
            fn = getattr(api, 'SteamAPI_ISteamUGC_' + name)
            fn.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_char_p]
            fn.restype = ctypes.c_bool
        api.SteamAPI_ISteamUGC_SubmitItemUpdate.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_char_p]
        api.SteamAPI_ISteamUGC_SubmitItemUpdate.restype = ctypes.c_uint64
        api.SteamAPI_ISteamUtils_IsAPICallCompleted.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.POINTER(ctypes.c_bool)]
        api.SteamAPI_ISteamUtils_IsAPICallCompleted.restype = ctypes.c_bool
        api.SteamAPI_ISteamUtils_GetAPICallResult.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_void_p,
                                                             ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_bool)]
        api.SteamAPI_ISteamUtils_GetAPICallResult.restype = ctypes.c_bool
        self.api = api

    def set_text(self, item, lang, title, description):
        api = self.api
        handle = api.SteamAPI_ISteamUGC_StartItemUpdate(self.ugc, APP_ID, item)
        for name, value in (('SetItemUpdateLanguage', lang), ('SetItemTitle', title), ('SetItemDescription', description)):
            if not getattr(api, 'SteamAPI_ISteamUGC_' + name)(self.ugc, handle, value.encode('utf-8')):
                raise SystemExit(f'{lang}: Steam refused {name}')
        call = api.SteamAPI_ISteamUGC_SubmitItemUpdate(self.ugc, handle, None)
        failed = ctypes.c_bool(False)
        deadline = time.time() + 120
        while not api.SteamAPI_ISteamUtils_IsAPICallCompleted(self.utils, call, ctypes.byref(failed)):
            api.SteamAPI_RunCallbacks()
            if time.time() > deadline:
                raise SystemExit(f'{lang}: no answer from Steam in two minutes')
            time.sleep(0.1)
        result = SubmitResult()
        api.SteamAPI_ISteamUtils_GetAPICallResult(self.utils, call, ctypes.byref(result), ctypes.sizeof(result),
                                                  SUBMIT_CALLBACK, ctypes.byref(failed))
        if failed.value or result.result != 1:
            raise SystemExit(f'{lang}: upload failed, EResult {result.result}')
        if result.needs_agreement:
            print('Steam says the Workshop legal agreement needs accepting.')
        print(f'{lang}: uploaded')

    def close(self):
        self.api.SteamAPI_Shutdown()


def upload(args):
    texts = build()
    only = None
    if '--only' in args:
        only = set(args[args.index('--only') + 1].split(','))
    english = '\n'.join(source_lines()) + '\n'
    item = item_id()
    with_english = '--no-english' not in args and (only is None or 'english' in only)
    if with_english and '--force' not in args:
        live = live_english(item).rstrip('\n') + '\n'
        last = open(UPLOADED, encoding='utf-8').read() if os.path.exists(UPLOADED) else None
        if live != english and live != last:
            raise SystemExit('The live English description differs from both the file and the last upload, '
                             'so it was edited on the website. Copy those edits into the file, or --force.')
    jobs = ([('english', english)] if with_english else []) + \
           [(lang, texts[lang]) for lang in LANGUAGES if only is None or lang in only]
    if '--dry' in args:
        for lang, text in jobs:
            print(f'would upload {lang}: {len(text)} characters')
        return
    steam = Steam()
    try:
        for lang, text in jobs:
            steam.set_text(item, lang, TITLE, text)
            if lang == 'english':
                with open(UPLOADED, 'w', encoding='utf-8') as f:
                    f.write(text)
    finally:
        steam.close()


if __name__ == '__main__':
    command = sys.argv[1] if len(sys.argv) > 1 else 'todo'
    if command == 'sync':
        sync()
    elif command == 'todo':
        todo()
    elif command == 'build':
        build()
    elif command == 'upload':
        upload(sys.argv[2:])
    else:
        print(__doc__)
