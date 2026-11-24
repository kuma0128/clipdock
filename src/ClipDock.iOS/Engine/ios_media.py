"""iPhone entry point. The player scripts run in the system JavaScriptCore framework."""
import _clipdock_js
from yt_dlp.extractor.youtube.jsc._builtin.ejs import EJSBaseJCP
from yt_dlp.extractor.youtube.jsc.provider import JsChallengeProviderError, register_provider

import media


@register_provider
class AppleJavaScriptCoreJCP(EJSBaseJCP):
    JS_RUNTIME_NAME = 'JavaScriptCore'

    def is_available(self):
        return self._available

    def _run_js_runtime(self, script):
        try:
            return _clipdock_js.evaluate(script)
        except ValueError as exc:
            raise JsChallengeProviderError(str(exc)) from exc


def run(link, directory, progress):
    return media.run(link, directory, progress, segmented=True)
