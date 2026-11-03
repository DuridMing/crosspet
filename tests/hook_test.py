# 鉤子指令碼的迴歸測試：python tests/hook_test.py
# 用沙箱資料目錄跑真的 crosspet-hook.py，只看它寫出了什麼、花了多久、有沒有報錯，不碰本機任何配置
#
# 多模型宿主（claude / gpt / qwen）按模型選角色、模型表與會話狀態分開存，以及角色還沒畫立繪時的降級。
# #19 的 stdin 讀取回歸測試在上游的 tests/hook_stdin_test.py 裡，這裡不再重複一份。
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
HOOK = REPO / 'integrations/crosspet-hook.py'


class HookModelHostTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.data = Path(self.tmp.name) / 'data'
        self.state = Path(self.tmp.name) / 'state'
        self.data.mkdir()
        self.state.mkdir()
    def tearDown(self):
        self.tmp.cleanup()
    def add_character(self, cid, idle=True):
        d = self.data / 'characters' / cid
        d.mkdir(parents=True)
        (d / 'character.json').write_text('{"name": "X"}', encoding='utf-8')
        if idle:
            (d / 'idle.webp').write_bytes(b'x')   # installed() 只看這張在不在，內容無所謂
        return d
    def fire(self, character, event):
        env = dict(os.environ, CROSSPET_DATA_DIR=str(self.data), CROSSPET_STATE_DIR=str(self.state))
        p = subprocess.run([sys.executable, str(HOOK), character], input=json.dumps(event).encode(),
                           capture_output=True, env=env)
        out = p.stderr.decode('utf-8', 'replace')
        self.assertNotIn('Traceback', out)
        self.assertEqual(p.returncode, 0, out)
        return p
    def read(self, name):
        try:
            return json.loads((self.state / name).read_text(encoding='utf-8'))
        except FileNotFoundError:
            return None

    # 宿主名同時也是角色名的多模型宿主（qwen）：<名字>-sessions.json 歸多會話狀態用，
    # 模型必須另記 <名字>-models.json。混在一起時第二條事件會把會話狀態當模型名讀，直接崩
    def test_named_multi_model_host_keeps_models_and_sessions_apart(self):
        self.add_character('qwen')
        self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': 's1', 'model': 'qwen3.8-max', 'source': 'startup'})
        self.assertEqual(self.read('qwen-host.json')['character'], 'qwen')
        # 第二條事件不帶模型名：要按會話記住的模型繼續判角色，而不是崩掉
        self.fire('qwen', {'hook_event_name': 'PreToolUse', 'session_id': 's1', 'tool_name': 'bash'})
        self.assertEqual(self.read('qwen-host.json')['character'], 'qwen')
        self.assertEqual(self.read('qwen-host.json')['model'], 'qwen3.8-max')
        self.assertEqual(self.read('qwen-state.json')['pose'], 'running')
        # 兩個檔案各管各的：會話狀態是 {pose,event,ts}，模型表是 {會話: 模型名}
        self.assertIn('pose', self.read('qwen-sessions.json')['s1'])
        self.assertEqual(self.read('qwen-models.json'), {'s1': 'qwen3.8-max'})
    def test_named_multi_model_host_survives_many_events(self):
        # 崩在「第二條事件」上，所以多來幾條
        self.add_character('qwen')
        self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': 's1', 'model': 'qwen3.8-max', 'source': 'startup'})
        for event in ('PreToolUse', 'PostToolUse', 'PreToolUse', 'Stop', 'UserPromptSubmit'):
            self.fire('qwen', {'hook_event_name': event, 'session_id': 's1', 'tool_name': 'read_file'})
    # 用倉庫裡真實的 characters/qwen/ 驗證交付物，而不只依賴 add_character 的佔點陣圖。
    # 立繪和接入是分開提交的：只有接入、還沒合入素材時，這裡就沒有可讀的角色檔案，跳過而不是失敗。
    @unittest.skipUnless((REPO / 'characters' / 'qwen' / 'idle.webp').exists(),
                         '需要 characters/qwen/ 裡的真實立繪')
    def test_bundled_qwen_character_is_selected(self):
        source = REPO / 'characters' / 'qwen'
        target = self.data / 'characters' / 'qwen'
        target.mkdir(parents=True)
        shutil.copy2(source / 'character.json', target / 'character.json')
        shutil.copy2(source / 'idle.webp', target / 'idle.webp')
        self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': 's1', 'model': 'qwen3.8-max'})
        self.assertEqual(self.read('qwen-host.json')['character'], 'qwen')
        self.assertEqual(self.read('qwen-state.json')['pose'], 'idle')
    def test_parallel_qwen_sessions_keep_each_model(self):
        # 並行鉤子不能爭用同一個臨時檔案，也不能在讀舊錶、寫新表時丟掉別的會話。
        self.add_character('qwen')
        def start(i):
            self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': f's{i}', 'model': f'qwen3.8-max-{i}'})
        with ThreadPoolExecutor(max_workers=8) as pool:
            list(pool.map(start, range(8)))
        self.assertEqual(self.read('qwen-models.json'), {f's{i}': f'qwen3.8-max-{i}' for i in range(8)})
    def test_model_without_character_falls_back_to_current(self):
        # 目標安裝目錄沒有立繪時：別報錯，交給當前角色來演
        self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': 's1', 'model': 'qwen3.8-max', 'source': 'startup'})
        self.assertIsNone(self.read('qwen-host.json')['character'])
        self.assertEqual(self.read('current-state.json')['event'], 'SessionStart')
    def test_other_provider_in_qwen_code_maps_to_its_character(self):
        self.add_character('qwen')
        self.add_character('glm')
        self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': 's1', 'model': 'glm-4.6', 'source': 'startup'})
        self.assertEqual(self.read('qwen-host.json')['character'], 'glm')
    # Qwen Code 的工具 id（來自它自帶的 docs/features/hooks.md 和工具名常量）：常用的都要演對動作
    def test_qwen_code_tool_ids_map_to_poses(self):
        self.add_character('qwen')
        self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': 's1', 'model': 'qwen3.8-max', 'source': 'startup'})
        expect = {'read_file': 'reading', 'grep_search': 'reading', 'glob': 'reading', 'list_directory': 'reading',
                  'zoom_image': 'reading', 'write_file': 'writing', 'edit': 'writing', 'run_shell_command': 'running',
                  'web_fetch': 'searching', 'web_search': 'searching', 'agent': 'delegating',
                  'ask_user_question': 'asking', 'image_gen': 'drawing'}
        got = {}
        for tool in expect:
            self.fire('qwen', {'hook_event_name': 'PreToolUse', 'session_id': 's1', 'tool_name': tool, 'tool_input': {}})
            got[tool] = self.read('qwen-state.json')['pose']
        self.assertEqual(got, expect)

    # 用千問賬號登入時，Qwen Code 報的模型名是別名 coder-model（它的 QWEN_OAUTH_MODELS，實為 Qwen 3.7 Max），10-19 本機實測收到
    def test_qwen_oauth_alias_coder_model_is_qwen(self):
        self.add_character('qwen')
        self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': 's1', 'model': 'coder-model', 'source': 'startup'})
        self.assertEqual(self.read('qwen-host.json')['character'], 'qwen')

    # 別的宿主裡用 Grok 模型（OpenRouter 的 x-ai/grok-…、xAI 自家的 grok-…）時換成 Grok
    def test_grok_model_is_grok(self):
        self.add_character('qwen')
        self.add_character('grok')
        for sid, model in (('s1', 'grok-4.7'), ('s2', 'x-ai/grok-code-fast-2')):
            self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': sid, 'model': model, 'source': 'startup'})
            self.assertEqual(self.read('qwen-host.json')['character'], 'grok', model)

    def test_session_start_writes_heartbeat(self):
        self.add_character('qwen')
        self.fire('qwen', {'hook_event_name': 'SessionStart', 'session_id': 's1', 'model': 'qwen3.8-max', 'source': 'startup'})
        self.assertTrue((self.state / 'seen-qwen').exists())



class HookGrokTests(unittest.TestCase):
    """Grok Build：欄位是駝峰寫法（sessionId / toolName / toolInput），每個鉤子程式都帶 GROK_HOOK_EVENT；
    它還會讀 ~/.claude/settings.json 裡的鉤子，那邊的 CrossPet 鉤子會被它以 claude 的名義觸發"""
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.data, self.state, self.grok_home = root / 'data', root / 'state', root / 'grok'
        for d in (self.data, self.state, self.grok_home):
            d.mkdir()
        for cid in ('grok', 'claude'):
            c = self.data / 'characters' / cid
            c.mkdir(parents=True)
            (c / 'character.json').write_text('{}', encoding='utf-8')
            (c / 'idle.webp').write_bytes(b'x')
    def tearDown(self):
        self.tmp.cleanup()
    def fire(self, character, event, grok=True):
        env = dict(os.environ, CROSSPET_DATA_DIR=str(self.data), CROSSPET_STATE_DIR=str(self.state), GROK_HOME=str(self.grok_home))
        env.pop('GROK_HOOK_EVENT', None)
        if grok:
            env['GROK_HOOK_EVENT'] = 'pre_tool_use'
        p = subprocess.run([sys.executable, str(HOOK), character], input=json.dumps(event).encode(), capture_output=True, env=env)
        self.assertEqual(p.returncode, 0, p.stderr.decode('utf-8', 'replace'))
    def read(self, name):
        try:
            return json.loads((self.state / name).read_text(encoding='utf-8'))
        except FileNotFoundError:
            return None

    def test_camel_case_tools(self):
        for tool, pose in (('run_terminal_command', 'running'), ('read_file', 'reading'), ('search_replace', 'writing'), ('web_search', 'searching')):
            self.fire('grok', {'hook_event_name': 'PreToolUse', 'sessionId': 's1', 'toolName': tool, 'toolInput': {}})
            self.assertEqual(self.read('grok-state.json')['pose'], pose, tool)

    def test_stop_cancelled_is_interrupt(self):
        self.fire('grok', {'hook_event_name': 'StopCancelled', 'sessionId': 's1', 'reason': 'user_interrupt'})
        self.assertEqual((self.read('grok-state.json')['pose'], self.read('grok-state.json')['event']), ('surprised', 'Stop'))

    def test_shutdown_stop_after_session_end_is_ignored(self):
        # 10-27 實測：會話關閉（SessionEnd）後 Grok 還會補發一個 reason=shutdown 的 Stop，不能把睡著的她拉回「開心」
        self.fire('grok', {'hook_event_name': 'SessionEnd', 'session_id': 's1', 'reason': 'shutdown'})
        self.fire('grok', {'hook_event_name': 'Stop', 'session_id': 's1', 'reason': 'shutdown', 'stop_hook_active': False})
        self.assertEqual(self.read('grok-state.json')['pose'], 'sleeping')
        # 真正的一輪結束（reason=end_turn）照常開心
        self.fire('grok', {'hook_event_name': 'Stop', 'session_id': 's2', 'reason': 'end_turn', 'promptId': 'p1'})
        self.assertEqual(self.read('grok-state.json')['pose'], 'happy')

    def test_permission_notification_is_asking(self):
        self.fire('grok', {'hook_event_name': 'Notification', 'sessionId': 's1', 'notificationType': 'permission_prompt'})
        self.assertEqual(self.read('grok-state.json')['pose'], 'asking')

    def test_claude_hook_fired_by_grok_plays_grok(self):
        # 只接了 Claude 鉤子、沒單獨接 Grok：Grok 幹活時由 Grok 來演，不能當成 Claude
        self.fire('claude', {'hook_event_name': 'PreToolUse', 'sessionId': 's1', 'toolName': 'read_file', 'toolInput': {}})
        self.assertEqual(self.read('grok-state.json')['pose'], 'reading')
        self.assertIsNone(self.read('claude-state.json'))

    def test_claude_hook_fired_by_grok_is_dropped_when_grok_is_integrated(self):
        # 兩邊都接了：Grok 自己那份會演，Claude 那份丟掉，免得演兩遍
        (self.grok_home / 'hooks').mkdir()
        (self.grok_home / 'hooks' / 'crosspet.json').write_text('{"hooks": {}}', encoding='utf-8')
        self.fire('claude', {'hook_event_name': 'PreToolUse', 'sessionId': 's1', 'toolName': 'read_file', 'toolInput': {}})
        self.assertIsNone(self.read('grok-state.json'))
        self.assertIsNone(self.read('claude-state.json'))

    def test_real_claude_is_untouched(self):
        self.fire('claude', {'hook_event_name': 'PreToolUse', 'session_id': 's1', 'tool_name': 'Read', 'tool_input': {}}, grok=False)
        self.assertEqual(self.read('claude-state.json')['pose'], 'reading')

if __name__ == '__main__':
    unittest.main()
