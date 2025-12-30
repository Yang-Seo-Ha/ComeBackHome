#pragma once
#include <opencv2/opencv.hpp>
#include <opencv2/dnn.hpp> // dnn 함수 사용을 위해 필수
#include <vector>
#include <string>

using namespace cv;
using namespace std;

// C#과 연동할 결과 구조체
struct DetectionResult {
    int label;  // 0: 사람, 1: 헬멧
    int isSafe; // 1: 안전, 0: 위험
    int x, y, w, h;
};

// 오류 해결: 누락되었던 confidenceThreshold 멤버 추가
struct DetectorConfig {
    float minConf;
    float headRatio;
    float colorThresh;
    float circleThresh;
    float edgeThresh;
    float overlapIoU;
    float finalConf;
    float confidenceThreshold; // 추가
};

class SafetyDetector {
protected:
    dnn::Net net;
    dnn::Net helmetNet;
    vector<DetectionResult> lastResults;

    // 오류 해결: 선언되지 않았던 멤버 변수 추가
    Rect lastRect;
    bool isFirstDetection = true;

public:
    SafetyDetector();
    virtual ~SafetyDetector() {}

    vector<DetectionResult> getLastResults() { return lastResults; }
    void clearResults() { lastResults.clear(); }
    void addResult(int label, int isSafe, Rect box) {
        DetectionResult res = { label, isSafe, box.x, box.y, box.width, box.height };
        lastResults.push_back(res);
    }

    // 모든 자식 클래스에서 이 형태를 똑같이 맞춰야 합니다.
    virtual void runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colors, const DetectorConfig& cfg) = 0;

    Rect scaleRect(const Rect& oriBox, int oriW, int oriH);
    Rect enlargeRectFixed(const Rect& in, int pw, int ph, int fc, int fr);
    bool detectHelmetByColor(const Mat& head, Mat& mask, float& conf);
    bool detectHelmetByShape(const Mat& head, vector<Vec3f>& circles, float& conf);
    bool detectHelmetByEdge(const Mat& head, Mat& edge, float& conf);
};

class LadderDetector : public SafetyDetector {
public:
    void runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colors, const DetectorConfig& cfg) override;
};

class PlatformDetector : public SafetyDetector {
public:
    void runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colors, const DetectorConfig& cfg) override;
};