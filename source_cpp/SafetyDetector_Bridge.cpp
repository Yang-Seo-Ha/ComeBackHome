#include "SafetyDetector.h"
#include <iostream>

// 오류 해결: vector를 반환하는 함수는 extern "C" 밖에 있어야 함
vector<pair<Scalar, Scalar>> GetDefaultVestColors() {
    vector<pair<Scalar, Scalar>> colors;
    colors.push_back({ Scalar(0, 100, 50), Scalar(10, 255, 255) });
    colors.push_back({ Scalar(20, 100, 100), Scalar(40, 255, 255) });
    return colors;
}

extern "C" {
    SafetyDetector* g_detector = nullptr;
    // 초기화 리스트에 confidenceThreshold 값도 포함시켰습니다.
    DetectorConfig ladderCfg = { 0.15f, 0.1f, 0.25f, 0.15f, 0.22f, 0.2f, 0.45f, 0.5f };
    DetectorConfig platformCfg = { 0.15f, 0.2f, 0.2f, 0.2f, 0.25f, 0.15f, 0.5f, 0.5f };

    __declspec(dllexport) void InitDetector(int mode) {
        if (g_detector) delete g_detector;
        if (mode == 1) g_detector = (SafetyDetector*)new LadderDetector();
        else g_detector = (SafetyDetector*)new PlatformDetector();
    }

    __declspec(dllexport) int ProcessSafety(const char* imagePath, int mode, DetectionResult* outResults) {
        if (!g_detector) return 0;

        Mat frame = imread(imagePath);
        if (frame.empty()) return 0;

        auto colors = GetDefaultVestColors();
        const DetectorConfig& currentCfg = (mode == 1) ? ladderCfg : platformCfg;

        g_detector->clearResults();
        g_detector->runDetection(frame, colors, currentCfg);

        vector<DetectionResult> results = g_detector->getLastResults();
        int count = 0;
        for (const auto& item : results) {
            if (count >= 50) break;
            outResults[count] = item;
            count++;
        }
        return count;
    }

    __declspec(dllexport) void ReleaseDetector() {
        if (g_detector) { delete g_detector; g_detector = nullptr; }
    }
}