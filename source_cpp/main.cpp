#include "SafetyDetector.h"
#include <iostream>

using namespace std;
using namespace cv;

int main() {
    // 1. 설정값 세팅
    // [사다리] 기존 최적화된 설정값
    DetectorConfig ladderCfg = { 0.15f, 0.1f, 0.25f, 0.15f, 0.22f, 0.2f, 0.45f };
    // [고소대] 하네스 탐지를 위해 영역을 조금 더 넓게 설정
    DetectorConfig platformCfg = { 0.15f, 0.2f, 0.2f, 0.2f, 0.25f, 0.15f, 0.5f };

    // 2. 경로 설정
    string ladderVideo = "C:/Users/dbsdm/Desktop/cppopencv/opencv1/TestImg/ladderImg/ladder.mp4";
    string platformVideo = "C:/Users/dbsdm/Desktop/cppopencv/opencv1/TestImg/Platform/platform.mp4";
    string ladderImage = "C:/Users/dbsdm/Desktop/cppopencv/opencv1/TestImg/ladderImg/frame_0024.jpg";

    // 3. 초기 상태 설정 (기본: 사다리 모드)
    SafetyDetector* detector = new LadderDetector();
    DetectorConfig currentCfg = ladderCfg;
    VideoCapture cap(ladderVideo);
    bool isImageMode = false; // 테스트 편의를 위해 영상 모드 우선 실행

    // 조끼/헬멧 색상 범위 (기존 유지)
    vector<pair<Scalar, Scalar>> vestColors;
    vestColors.push_back({ Scalar(0, 100, 50), Scalar(10, 255, 255) });    // 빨강
    vestColors.push_back({ Scalar(170, 100, 50), Scalar(180, 255, 255) });
    vestColors.push_back({ Scalar(20, 100, 100), Scalar(40, 255, 255) });  // 노랑
    vestColors.push_back({ Scalar(40, 100, 100), Scalar(80, 255, 255) });  // 형광

    cout << "▶ [모드 확인] 현재: 사다리(Ladder) 탐지 모드" << endl;
    cout << "   (키보드 1: 사다리 전환, 2: 고소대 전환, ESC: 종료)" << endl;

    while (true) {
        Mat frame;
        if (isImageMode) {
            frame = imread(ladderImage);
        }
        else {
            cap >> frame;
            if (frame.empty()) {
                cap.set(CAP_PROP_POS_FRAMES, 0); // 무한 반복 재생
                continue;
            }
        }

        // ⭐ 분석 및 통합 대시보드 출력 실행
        // 각 Detector 내부의 runDetection에서 imshow("Safety Integration Dashboard", canvas)를 수행함
        detector->runDetection(frame, vestColors, currentCfg);

        int key = waitKey(isImageMode ? 0 : 33);

        // 4. 모드 전환 로직
        if (key == '1') {
            // 사다리 모드로 전환
            cout << "🔄 [모드 변경] 사다리(Ladder) 작업 탐지" << endl;
            delete detector;
            detector = new LadderDetector();
            currentCfg = ladderCfg;
            isImageMode = false;
            cap.open(ladderVideo);
            detector->resetState();
        }
        else if (key == '2') {
            // 고소대 모드로 전환
            cout << "🔄 [모드 변경] 고소작업대(Platform) 작업 탐지" << endl;
            delete detector;
            detector = new PlatformDetector(); // ⭐ 새로 만든 고소대 클래스 생성
            currentCfg = platformCfg;
            isImageMode = false;
            cap.open(platformVideo); // ⭐ 고소대 영상 오픈
            detector->resetState();
        }
        else if (key == 27) break; // ESC 종료
    }

    delete detector;
    return 0;
}