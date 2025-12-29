#include "SafetyDetector.h"
#include <iostream>

using namespace cv;
using namespace std;

// 전역 변수로 디텍터 관리
SafetyDetector* g_detector = nullptr;

// 기존 메인에 있던 최적 설정값들 유지
DetectorConfig ladderCfg = { 0.15f, 0.1f, 0.25f, 0.15f, 0.22f, 0.2f, 0.45f };
DetectorConfig platformCfg = { 0.15f, 0.2f, 0.2f, 0.2f, 0.25f, 0.15f, 0.5f };

// 기존 색상 범위 유지
vector<pair<Scalar, Scalar>> GetDefaultVestColors() {
    vector<pair<Scalar, Scalar>> colors;
    colors.push_back({ Scalar(0, 100, 50), Scalar(10, 255, 255) });    // 빨강
    colors.push_back({ Scalar(170, 100, 50), Scalar(180, 255, 255) });
    colors.push_back({ Scalar(20, 100, 100), Scalar(40, 255, 255) });  // 노랑
    colors.push_back({ Scalar(40, 100, 100), Scalar(80, 255, 255) });  // 형광
    return colors;
}

extern "C" {
    // 1. 디텍터 초기화
    __declspec(dllexport) void InitDetector(int mode) {
        if (g_detector) delete g_detector;
        if (mode == 1) g_detector = new LadderDetector();
        else g_detector = new PlatformDetector();
    }

    // 2. 한 프레임 이미지 탐지 실행 (C#에서 경로를 넘겨받음)
    __declspec(dllexport) void ProcessSafety(const char* imagePath, int mode) {
        if (!g_detector) return;

        Mat frame = imread(imagePath);
        if (frame.empty()) {
            cout << "❌ 이미지를 찾을 수 없습니다: " << imagePath << endl;
            return;
        }

        auto colors = GetDefaultVestColors();
        DetectorConfig currentCfg = (mode == 1) ? ladderCfg : platformCfg;

        // 실제 모든 탐지 및 imshow 대시보드 출력 실행
        g_detector->runDetection(frame, colors, currentCfg);
    }

    // 3. 메모리 해제
    __declspec(dllexport) void ReleaseDetector() {
        if (g_detector) {
            delete g_detector;
            g_detector = nullptr;
        }
    }
}