#pragma once
#include <opencv2/opencv.hpp>
#include <opencv2/dnn.hpp>
#include <vector>
#include <string>

using namespace cv;
using namespace cv::dnn;
using namespace std;

// 설정값 구조체
struct DetectorConfig {
    float confidenceThreshold;
    float helmetColorConf;
    float helmetShapeConf;
    float helmetEdgeConf;
    float vestColorConf;
    float vestEdgeConf;
    float finalConf;
};

// 부모 클래스
class SafetyDetector {
protected:
    Net net;
    Net helmetNet;
    Rect lastRect;
    bool isFirstDetection = true;

public:
    SafetyDetector();
    virtual ~SafetyDetector() {}

    virtual vector<Rect> runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) = 0;

    void resetState() { isFirstDetection = true; }

    // 공통 유틸 함수
    Rect scaleRect(const Rect& oriBox, int oriW, int oriH);
    Rect enlargeRectFixed(const Rect& in, int pw, int ph, int fc, int fr);
    bool detectHelmetByColor(const Mat& head, Mat& mask, float& conf);
    bool detectHelmetByShape(const Mat& head, vector<Vec3f>& circles, float& conf);
    bool detectHelmetByEdge(const Mat& head, Mat& edge, float& conf);
};

// 사다리 디텍터
class LadderDetector : public SafetyDetector {
public:
    vector<Rect> runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) override;
};

// 고소대 디텍터
class PlatformDetector : public SafetyDetector {
public:
    vector<Rect> runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) override;
};