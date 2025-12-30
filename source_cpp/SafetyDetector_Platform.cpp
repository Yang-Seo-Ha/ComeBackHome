#include "SafetyDetector.h"
#include <opencv2/dnn.hpp>

using namespace cv;
using namespace cv::dnn;

void PlatformDetector::runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) {
    if (frame.empty()) return;

    // 1. 사람 탐지
    Mat blob = blobFromImage(frame, 0.007843, Size(300, 300), Scalar(127.5, 127.5, 127.5));
    net.setInput(blob);
    Mat prob = net.forward();
    Mat detM(prob.size[2], prob.size[3], CV_32F, prob.ptr<float>());

    for (int i = 0; i < detM.rows; i++) {
        if ((int)detM.at<float>(i, 1) == 15 && detM.at<float>(i, 2) > cfg.confidenceThreshold) {
            int x1 = (int)(detM.at<float>(i, 3) * frame.cols);
            int y1 = (int)(detM.at<float>(i, 4) * frame.rows);
            int x2 = (int)(detM.at<float>(i, 5) * frame.cols);
            int y2 = (int)(detM.at<float>(i, 6) * frame.rows);
            Rect personRect(Point(x1, y1), Point(x2, y2));
            personRect &= Rect(0, 0, frame.cols, frame.rows);

            // 2. 헬멧 검사 (고소대 특성상 상단 영역 집중 검사)
            Rect hROI = Rect(personRect.x, personRect.y, personRect.width, (int)(personRect.height * 0.25)) & Rect(0, 0, frame.cols, frame.rows);

            bool helmetOk = false;
            if (hROI.width > 10) {
                Mat head = frame(hROI), m; float c;
                helmetOk = detectHelmetByColor(head, m, c);
            }

            // ✅ 결과 데이터 전송
            this->addResult(0, helmetOk ? 1 : 0, personRect);
        }
    }
}