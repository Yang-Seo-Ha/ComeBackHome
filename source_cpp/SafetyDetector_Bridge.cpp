#include "SafetyDetector.h"

// 전역 변수로 디텍터 관리 (필요시)
SafetyDetector* g_detector = nullptr;

// C#에서 호출할 수 있도록 이름을 고정 (Name Mangling 방지)
extern "C" {
    // 1. 디텍터 초기화 (1: 사다리, 2: 고소대)
    __declspec(dllexport) void InitDetector(int mode) {
        if (g_detector) delete g_detector;

        if (mode == 1) g_detector = new LadderDetector();
        else g_detector = new PlatformDetector();
    }

    // 2. 한 프레임 연산 실행 (C#에서 이미지 데이터 등을 넘겨받을 수도 있음)
    __declspec(dllexport) void ProcessSafety() {
        if (!g_detector) return;

        // 기존에 작성한 탐지 로직 실행
        // (현재 구조에서는 내부에서 VideoCapture와 imshow를 수행함)
        // Mat dummy; vector<pair<Scalar, Scalar>> colors; DetectorConfig cfg;
        // g_detector->runDetection(dummy, colors, cfg);
    }

    // 3. 종료 시 메모리 해제
    __declspec(dllexport) void ReleaseDetector() {
        if (g_detector) {
            delete g_detector;
            g_detector = nullptr;
        }
    }
}