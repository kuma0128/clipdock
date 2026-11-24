#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN
// Called on a worker thread. Returning NO from progress cancels the transfer.
NSString * _Nullable CDDownload(NSString *link, NSString *directory, BOOL (^progress)(NSInteger));
NS_ASSUME_NONNULL_END
