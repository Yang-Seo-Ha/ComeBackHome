// SafetyDetector_Bridge.cpp
#include "SafetyDetector.h"

SafetyDetector* g_detector = nullptr;

extern "C" {
    // 1. 디렉터 초기화 (모드 선택)
    __declspec(dllexport) void InitDetector(int mode) {
        if (g_detector) delete g_detector;
        if (mode == 1) g_detector = new LadderDetector();
        else g_detector = new PlatformDetector();
    }

    // 2. 탐지 루프 실행 (C# 이벤트에 의해 호출됨)
    __declspec(dllexport) void RunDetectionLoop() {
        if (!g_detector) return;
        // 기존에 만든 while(true)가 포함된 탐지 로직 실행
        // g_detector->runDetection(...); 
    }

    // 3. 종료
    __declspec(dllexport) void ReleaseDetector() {
        if (g_detector) { delete g_detector; g_detector = nullptr; }
    }
}