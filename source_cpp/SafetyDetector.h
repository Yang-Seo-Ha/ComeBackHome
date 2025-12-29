#pragma once
#include <opencv2/opencv.hpp>
#include <opencv2/dnn.hpp>
#include <vector>
#include <string>

using namespace cv;
using namespace dnn;
using namespace std;

struct DetectorConfig {
    float confidenceThreshold;
    float padUp, padDown, padSide;
    float headHeightRate, bodyStartRate, bodyHeightRate;
};

class SafetyDetector {
protected:
    Net net; Net helmetNet; Rect lastRect; bool isFirstDetection = true; int missingFrames = 0;
    // 보조 함수 (헬멧 전용으로 통합)
    Rect enlargeRectFixed(const Rect& inputRect, int padW, int padH, int frameCols, int frameRows);
    Rect scaleRect(const Rect& originalBox, int oriW, int oriH);
    bool detectHelmetByColor(const Mat& headRegion, Mat& outMask, float& confidence);
    bool detectHelmetByShape(const Mat& headRegion, vector<Vec3f>& circles, float& confidence);
    bool detectHelmetByEdge(const Mat& headRegion, Mat& outEdges, float& confidence);

public:
    SafetyDetector();
    virtual ~SafetyDetector() {}
    virtual vector<Rect> runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) = 0;
    void resetState() { isFirstDetection = true; missingFrames = 0; lastRect = Rect(); }
};

class LadderDetector : public SafetyDetector {
public:
    vector<Rect> runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) override;
};

class PlatformDetector : public SafetyDetector {
public:
    // 하네스 탐지 함수 제거됨
    vector<Rect> runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) override;
};